using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Processors;

namespace redb.Route.Tests.Processors;

/// <summary>
/// Tests verifying DI scope lifecycle across processors:
/// - Sequential children share parent scope (same connection, same TX)
/// - Parallel children get own scopes, released per-part
/// - ReleaseScopes is idempotent and thread-safe
/// </summary>
public class ScopeLifecycleTests
{
    // ── Shared helpers ──

    private class ScopeTracker : IDisposable
    {
        public bool IsDisposed { get; private set; }
        public Guid Id { get; } = Guid.NewGuid();
        public void Dispose() => IsDisposed = true;
    }

    private static (IServiceScopeFactory factory, ServiceProvider root) CreateTrackedFactory()
    {
        var services = new ServiceCollection()
            .AddScoped<ScopeTracker>()
            .BuildServiceProvider();
        return (services.GetRequiredService<IServiceScopeFactory>(), services);
    }

    // ══════════════════════════════════════════════════════════════
    // Exchange.CreateLinkedChild / CloneLinked — scope sharing
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public void CreateLinkedChild_SharesParentServiceProvider()
    {
        var (factory, root) = CreateTrackedFactory();
        using var _ = root;

        var parent = Exchange.Create(new Message("parent"), factory);
        var child = parent.CreateLinkedChild(new Message("child"));

        child.ServiceProvider.Should().BeSameAs(parent.ServiceProvider);
    }

    [Fact]
    public void CloneLinked_SharesParentServiceProvider()
    {
        var (factory, root) = CreateTrackedFactory();
        using var _ = root;

        var parent = Exchange.Create(new Message("parent"), factory);
        var clone = parent.CloneLinked();

        clone.ServiceProvider.Should().BeSameAs(parent.ServiceProvider);
    }

    [Fact]
    public async Task LinkedChild_Dispose_DoesNotDisposeParentScope()
    {
        var (factory, root) = CreateTrackedFactory();
        using var _ = root;

        var parent = Exchange.Create(new Message("parent"), factory);
        var tracker = parent.ServiceProvider!.GetRequiredService<ScopeTracker>();

        var child = parent.CreateLinkedChild(new Message("child"));
        await child.DisposeAsync();

        // Parent scope must still work
        tracker.IsDisposed.Should().BeFalse();
        parent.ServiceProvider.Should().NotBeNull();
    }

    [Fact]
    public async Task LinkedClone_Dispose_DoesNotDisposeParentScope()
    {
        var (factory, root) = CreateTrackedFactory();
        using var _ = root;

        var parent = Exchange.Create(new Message("parent"), factory);
        var tracker = parent.ServiceProvider!.GetRequiredService<ScopeTracker>();

        var clone = parent.CloneLinked();
        await clone.DisposeAsync();

        tracker.IsDisposed.Should().BeFalse();
        parent.ServiceProvider.Should().NotBeNull();
    }

    // ══════════════════════════════════════════════════════════════
    // ReleaseScopes — idempotency and thread safety
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ReleaseScopes_CalledTwice_IsIdempotent()
    {
        var (factory, root) = CreateTrackedFactory();
        using var _ = root;

        var exchange = Exchange.Create(new Message(), factory);
        await exchange.ReleaseScopes();
        await exchange.ReleaseScopes(); // should not throw

        exchange.ServiceProvider.Should().BeNull();
    }

    [Fact]
    public async Task ReleaseScopes_ConcurrentCalls_NoDoubleDispose()
    {
        var (factory, root) = CreateTrackedFactory();
        using var _ = root;

        var exchange = Exchange.Create(new Message(), factory);

        // Hammer from multiple threads
        var tasks = Enumerable.Range(0, 50)
            .Select(_ => Task.Run(async () => await exchange.ReleaseScopes()))
            .ToArray();
        await Task.WhenAll(tasks);

        exchange.ServiceProvider.Should().BeNull();
    }

    [Fact]
    public async Task ReleaseScopes_KeepsBodyAlive()
    {
        var (factory, root) = CreateTrackedFactory();
        using var _ = root;

        var exchange = Exchange.Create(new Message("important-data"), factory);
        exchange.In.Headers["key"] = "value";

        await exchange.ReleaseScopes();

        exchange.In.Body.Should().Be("important-data");
        exchange.In.Headers["key"].Should().Be("value");
    }

    // ══════════════════════════════════════════════════════════════
    // Splitter — sequential shares scope, parallel releases early
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Splitter_Sequential_ChildrenShareParentScope()
    {
        var (factory, root) = CreateTrackedFactory();
        using var _ = root;
        var observedProviders = new List<IServiceProvider>();

        var splitter = new SplitterProcessor(
            ex => ((int[])ex.In.Body!).Cast<object?>(),
            new DelegateProcessor(ex =>
            {
                observedProviders.Add(ex.ServiceProvider!);
            }),
            parallelProcessing: false);

        var exchange = Exchange.Create(new Message(new[] { 1, 2, 3 }), factory);
        var parentProvider = exchange.ServiceProvider;

        await splitter.Process(exchange);

        observedProviders.Should().HaveCount(3);
        // All sequential children share parent's ServiceProvider
        observedProviders.Should().AllSatisfy(sp => sp.Should().BeSameAs(parentProvider));
    }

    [Fact]
    public async Task Splitter_Parallel_EachChildGetsOwnScope()
    {
        var (factory, root) = CreateTrackedFactory();
        using var _ = root;
        var observedIds = new ConcurrentBag<Guid>();

        var splitter = new SplitterProcessor(
            ex => ((int[])ex.In.Body!).Cast<object?>(),
            new DelegateProcessor(ex =>
            {
                var tracker = ex.ServiceProvider!.GetRequiredService<ScopeTracker>();
                observedIds.Add(tracker.Id);
            }),
            parallelProcessing: true,
            maxDegreeOfParallelism: 4);

        var exchange = Exchange.Create(new Message(new[] { 1, 2, 3, 4 }), factory);
        var parentTracker = exchange.ServiceProvider!.GetRequiredService<ScopeTracker>();

        await splitter.Process(exchange);

        // Each parallel child should have its own scope (distinct tracker IDs)
        observedIds.Distinct().Should().HaveCount(4);
        // None should be the parent's
        observedIds.Should().NotContain(parentTracker.Id);
    }

    [Fact]
    public async Task Splitter_Parallel_ScopesReleasedAfterProcessing()
    {
        var (factory, root) = CreateTrackedFactory();
        using var _ = root;
        var trackers = new ConcurrentBag<ScopeTracker>();

        var splitter = new SplitterProcessor(
            ex => ((int[])ex.In.Body!).Cast<object?>(),
            new DelegateProcessor(ex =>
            {
                var tracker = ex.ServiceProvider!.GetRequiredService<ScopeTracker>();
                trackers.Add(tracker);
            }),
            parallelProcessing: true);

        var exchange = Exchange.Create(new Message(new[] { 1, 2, 3 }), factory);
        await splitter.Process(exchange);

        // All child scopes should be disposed after processing
        trackers.Should().HaveCount(3);
        trackers.Should().AllSatisfy(t => t.IsDisposed.Should().BeTrue());
    }

    // ══════════════════════════════════════════════════════════════
    // Multicast — sequential shares scope, parallel releases early
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Multicast_Sequential_ChildrenShareParentScope()
    {
        var (factory, root) = CreateTrackedFactory();
        using var _ = root;
        var observedProviders = new List<IServiceProvider>();

        var mc = new MulticastProcessor(parallelProcessing: false);
        mc.AddTarget(new DelegateProcessor(ex => observedProviders.Add(ex.ServiceProvider!)));
        mc.AddTarget(new DelegateProcessor(ex => observedProviders.Add(ex.ServiceProvider!)));
        mc.AddTarget(new DelegateProcessor(ex => observedProviders.Add(ex.ServiceProvider!)));

        var exchange = Exchange.Create(new Message("test"), factory);
        var parentProvider = exchange.ServiceProvider;

        await mc.Process(exchange);

        observedProviders.Should().HaveCount(3);
        observedProviders.Should().AllSatisfy(sp => sp.Should().BeSameAs(parentProvider));
    }

    [Fact]
    public async Task Multicast_Parallel_EachChildGetsOwnScope()
    {
        var (factory, root) = CreateTrackedFactory();
        using var _ = root;
        var observedIds = new ConcurrentBag<Guid>();

        var mc = new MulticastProcessor(parallelProcessing: true, maxDegreeOfParallelism: 3);
        for (var i = 0; i < 3; i++)
        {
            mc.AddTarget(new DelegateProcessor(ex =>
            {
                var tracker = ex.ServiceProvider!.GetRequiredService<ScopeTracker>();
                observedIds.Add(tracker.Id);
            }));
        }

        var exchange = Exchange.Create(new Message("test"), factory);
        var parentTracker = exchange.ServiceProvider!.GetRequiredService<ScopeTracker>();

        await mc.Process(exchange);

        observedIds.Distinct().Should().HaveCount(3);
        observedIds.Should().NotContain(parentTracker.Id);
    }

    [Fact]
    public async Task Multicast_Parallel_ScopesReleasedAfterProcessing()
    {
        var (factory, root) = CreateTrackedFactory();
        using var _ = root;
        var trackers = new ConcurrentBag<ScopeTracker>();

        var mc = new MulticastProcessor(parallelProcessing: true, maxDegreeOfParallelism: 3);
        for (var i = 0; i < 3; i++)
        {
            mc.AddTarget(new DelegateProcessor(ex =>
            {
                trackers.Add(ex.ServiceProvider!.GetRequiredService<ScopeTracker>());
            }));
        }

        var exchange = Exchange.Create(new Message("test"), factory);
        await mc.Process(exchange);

        trackers.Should().HaveCount(3);
        trackers.Should().AllSatisfy(t => t.IsDisposed.Should().BeTrue());
    }

    // ══════════════════════════════════════════════════════════════
    // Loop — copy mode shares scope by default, iterations disposed
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Loop_CopyMode_ShareScope_IterationsShareParentScope()
    {
        var (factory, root) = CreateTrackedFactory();
        using var _ = root;
        var observedProviders = new List<IServiceProvider>();

        var loop = new LoopProcessor(
            new DelegateProcessor(ex => observedProviders.Add(ex.ServiceProvider!)),
            3, copy: true, shareScope: true);

        var exchange = Exchange.Create(new Message("data"), factory);
        var parentProvider = exchange.ServiceProvider;

        await loop.Process(exchange);

        observedProviders.Should().HaveCount(3);
        // snapshot is CloneLinked, iteration targets are CloneLinked from snapshot
        // all should resolve from the same scope as parent
        observedProviders.Should().AllSatisfy(sp => sp.Should().BeSameAs(parentProvider));
    }

    [Fact]
    public async Task Loop_CopyMode_NoShareScope_IterationsGetOwnScope()
    {
        var (factory, root) = CreateTrackedFactory();
        using var _ = root;
        var observedIds = new List<Guid>();

        var loop = new LoopProcessor(
            new DelegateProcessor(ex =>
            {
                observedIds.Add(ex.ServiceProvider!.GetRequiredService<ScopeTracker>().Id);
            }),
            3, copy: true, shareScope: false);

        var exchange = Exchange.Create(new Message("data"), factory);
        var parentId = exchange.ServiceProvider!.GetRequiredService<ScopeTracker>().Id;

        await loop.Process(exchange);

        observedIds.Should().HaveCount(3);
        // All iterations share snapshot scope (not parent, not each other — snapshot has own scope)
        // With shareScope=false, snapshot = Clone() (own scope), iterations = CloneLinked from snapshot
        // So all 3 see snapshot's tracker, but NOT parent's
        observedIds.Should().AllSatisfy(id => id.Should().NotBe(parentId));
    }

    // ══════════════════════════════════════════════════════════════
    // StreamingSplitter — sequential, shares parent scope
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public async Task StreamingSplitter_ChildrenShareParentScope()
    {
        var (factory, root) = CreateTrackedFactory();
        using var _ = root;
        var observedProviders = new List<IServiceProvider>();

        var splitter = new StreamingSplitterProcessor(
            ex => ToAsyncEnumerable((int[])ex.In.Body!),
            new DelegateProcessor(ex => observedProviders.Add(ex.ServiceProvider!)));

        var exchange = Exchange.Create(new Message(new[] { 1, 2, 3 }), factory);
        var parentProvider = exchange.ServiceProvider;

        await splitter.Process(exchange);

        observedProviders.Should().HaveCount(3);
        observedProviders.Should().AllSatisfy(sp => sp.Should().BeSameAs(parentProvider));
    }

    private static async IAsyncEnumerable<object?> ToAsyncEnumerable(int[] items)
    {
        foreach (var item in items)
        {
            await Task.Yield();
            yield return item;
        }
    }

    // ══════════════════════════════════════════════════════════════
    // Enricher — shares parent scope
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Enrich_Clone_SharesParentScope()
    {
        var (factory, root) = CreateTrackedFactory();
        using var _ = root;

        var parent = Exchange.Create(new Message("data"), factory);
        var parentProvider = parent.ServiceProvider;

        // CloneLinked is what EnrichProcessor now uses internally
        var clone = parent.CloneLinked();
        clone.ServiceProvider.Should().BeSameAs(parentProvider);

        await clone.DisposeAsync();
        parent.ServiceProvider.Should().NotBeNull();
    }
}

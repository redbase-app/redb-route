using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using redb.Route.Abstractions;
using redb.Route.Components;
using redb.Route.Core;
using redb.Route.Processors;

namespace redb.Route.Tests.Processors;

/// <summary>
/// The exchange's own bookkeeping — a cached named DI scope (<c>__redb_scope:*</c>) or a registered resource
/// (<c>__redb_resource:*</c>) — belongs to the exchange that created it. A branch that caches a scope on its clone
/// must not hand that scope to the original through the aggregation merge-back: the clone releases it right after,
/// and the original would keep a disposed scope under the same key (ObjectDisposedException on the next use).
/// </summary>
public class OwnedPropertiesMergeBackTests
{
    private const string ScopeKey = "__redb_scope:audit";

    private sealed class TrackingScope : IServiceScope, IAsyncDisposable
    {
        public int DisposeCount;
        public IServiceProvider ServiceProvider => null!;
        public void Dispose() => Interlocked.Increment(ref DisposeCount);
        public ValueTask DisposeAsync() { Interlocked.Increment(ref DisposeCount); return ValueTask.CompletedTask; }
    }

    [Fact]
    public async Task Multicast_BranchScope_StaysWithTheClone()
    {
        var scope = new TrackingScope();
        var multicast = new MulticastProcessor(parallelProcessing: false, aggregationStrategy: (_, latest) => latest)
            .AddTarget(new DelegateProcessor(ex => ex.Properties[ScopeKey] = scope));
        var original = new Exchange(new Message("m"));

        await multicast.Process(original);

        original.Properties.Should().NotContainKey(ScopeKey, "the clone owns the scope it cached and has released it");
        scope.DisposeCount.Should().Be(1, "released once, with the clone");
    }

    [Fact]
    public async Task Loop_IterationScope_StaysWithTheIterationCopy()
    {
        var scope = new TrackingScope();
        var loop = new LoopProcessor(new DelegateProcessor(ex => ex.Properties[ScopeKey] = scope), 1, copy: true);
        var original = new Exchange(new Message("m"));

        await loop.Process(original);

        original.Properties.Should().NotContainKey(ScopeKey, "the iteration copy owns the scope it cached and has released it");
        scope.DisposeCount.Should().Be(1, "released once, with the iteration copy");
    }

    [Fact]
    public async Task Splitter_PartScope_StaysWithThePart()
    {
        var scope = new TrackingScope();
        var splitter = new SplitterProcessor(
            ex => (IEnumerable<object?>)ex.In.Body!,
            new DelegateProcessor(ex => ex.Properties[ScopeKey] = scope),
            aggregationStrategy: (_, part) => part);
        var original = new Exchange(new Message(new List<object?> { "a" }));

        await splitter.Process(original);

        original.Properties.Should().NotContainKey(ScopeKey, "the part owns the scope it cached and has released it");
        scope.DisposeCount.Should().Be(1, "released once, with the part");
    }

    [Fact]
    public async Task ScatterGather_RecipientScope_StaysWithTheClone()
    {
        var scope = new TrackingScope();
        var context = new RouteContext();
        var consumers = new List<IConsumer>();
        foreach (var name in new[] { "direct:owned-sg-a", "direct:owned-sg-b" })
        {
            var consumer = ((DirectEndpoint)context.GetEndpoint(name)).CreateConsumer(new DelegateProcessor(ex => ex.In.Headers["seen"] = true));
            await consumer.Start();
            consumers.Add(consumer);
        }
        try
        {
            // A recipient runs on a child exchange (its scopes stay there); the aggregation strategy runs on the
            // scatter clone itself and may touch redb — that scope must not reach the original either.
            var sg = new ScatterGatherProcessor(context, ["direct:owned-sg-a", "direct:owned-sg-b"], (_, latest) => { latest.Properties[ScopeKey] = scope; return latest; });
            var original = new Exchange(new Message("m"));

            await sg.Process(original);

            original.Properties.Should().NotContainKey(ScopeKey, "the recipient clone owns the scope it cached and has released it");
            scope.DisposeCount.Should().Be(1, "released once, with the recipient clone");
        }
        finally
        {
            foreach (var consumer in consumers) await consumer.Stop();
            await context.DisposeAsync();
        }
    }
}

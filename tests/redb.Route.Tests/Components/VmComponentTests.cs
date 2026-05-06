using System.Collections.Concurrent;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using redb.Route.Abstractions;
using redb.Route.Components;
using redb.Route.Core;

namespace redb.Route.Tests.Components;

/// <summary>
/// Tests for DirectVm (cross-context synchronous) component.
/// </summary>
public class DirectVmComponentTests : IAsyncDisposable
{
    private readonly SharedVmRegistry _registry = new();
    private readonly ServiceProvider _sp;

    public DirectVmComponentTests()
    {
        var sc = new ServiceCollection();
        sc.AddSingleton(_registry);
        _sp = sc.BuildServiceProvider();
    }

    public async ValueTask DisposeAsync()
    {
        _sp.Dispose();
        GC.SuppressFinalize(this);
    }

    private RouteContext CreateContext(string id) => new(_sp, id);

    [Fact]
    public void Component_HasCorrectScheme()
    {
        var component = new DirectVmComponent();
        component.Scheme.Should().Be("direct-vm");
    }

    [Fact]
    public void CreateEndpoint_ReturnsDirectVmEndpoint()
    {
        var component = new DirectVmComponent();
        var uri = EndpointUriParser.Parse("direct-vm://myep");
        var endpoint = component.CreateEndpoint(uri);
        endpoint.Should().BeOfType<DirectVmEndpoint>();
    }

    [Fact]
    public void CreateEndpoint_SameUri_ReturnsSameInstance()
    {
        var component = new DirectVmComponent();
        var uri1 = EndpointUriParser.Parse("direct-vm://myep");
        var uri2 = EndpointUriParser.Parse("direct-vm://myep");
        var ep1 = component.CreateEndpoint(uri1);
        var ep2 = component.CreateEndpoint(uri2);
        ep1.Should().BeSameAs(ep2);
    }

    [Fact]
    public async Task CrossContext_ProducerInvokesConsumer()
    {
        object? received = null;

        await using var ctxA = CreateContext("ctx-a");
        await using var ctxB = CreateContext("ctx-b");

        // Context A: consumer on direct-vm://shared
        ctxA.AddRoutes(r =>
        {
            r.From("direct-vm://shared")
                .Process(e => received = e.In.Body);
        });

        // Context B: producer sends to direct-vm://shared
        ctxB.AddRoutes(r =>
        {
            r.From("direct://trigger")
                .To("direct-vm://shared");
        });

        await ctxA.Start();
        await ctxB.Start();

        var producer = ctxB.GetEndpoint("direct://trigger").CreateProducer();
        await producer.Start();
        await producer.Process(new Exchange(new Message { Body = "hello-vm" }));

        received.Should().Be("hello-vm");
    }

    [Fact]
    public async Task SameContext_ProducerInvokesConsumer()
    {
        object? received = null;

        await using var ctx = CreateContext("ctx-single");

        ctx.AddRoutes(r =>
        {
            r.From("direct-vm://local")
                .Process(e => received = e.In.Body);
        });

        ctx.AddRoutes(r =>
        {
            r.From("direct://input")
                .To("direct-vm://local");
        });

        await ctx.Start();

        var producer = ctx.GetEndpoint("direct://input").CreateProducer();
        await producer.Start();
        await producer.Process(new Exchange(new Message { Body = "local-msg" }));

        received.Should().Be("local-msg");
    }

    [Fact]
    public async Task NoConsumer_ThrowsInvalidOperation()
    {
        await using var ctx = CreateContext("ctx-no-consumer");

        ctx.AddRoutes(r =>
        {
            r.From("direct://input")
                .To("direct-vm://orphan");
        });

        await ctx.Start();

        var producer = ctx.GetEndpoint("direct://input").CreateProducer();
        await producer.Start();

        var act = () => producer.Process(new Exchange(new Message { Body = "x" }));
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*No consumer registered*orphan*");
    }

    [Fact]
    public async Task DuplicateConsumer_SecondRegistrationFails()
    {
        await using var ctxA = CreateContext("ctx-dup-a");
        await using var ctxB = CreateContext("ctx-dup-b");

        ctxA.AddRoutes(r =>
        {
            r.From("direct-vm://unique")
                .Process(_ => { });
        });

        ctxB.AddRoutes(r =>
        {
            r.From("direct-vm://unique")
                .Process(_ => { });
        });

        await ctxA.Start();
        // ctxB.Start swallows the exception (RouteContext logs + continues)
        await ctxB.Start();

        // Only the first registration should be in the registry
        _registry.GetProcessor("direct-vm://unique").Should().NotBeNull();

        // Stop ctxA → unregisters, then ctxB can't have replaced it
        await ctxA.Stop();
        _registry.GetProcessor("direct-vm://unique").Should().BeNull();
    }

    [Fact]
    public async Task Stop_UnregistersProcessor_NewConsumerCanRegister()
    {
        object? received = null;

        await using var ctxA = CreateContext("ctx-reuse-a");
        await using var ctxB = CreateContext("ctx-reuse-b");

        ctxA.AddRoutes(r =>
        {
            r.From("direct-vm://reuse")
                .Process(e => received = e.In.Body);
        });

        await ctxA.Start();
        await ctxA.Stop();

        // Now ctxB can take over the same name
        ctxB.AddRoutes(r =>
        {
            r.From("direct-vm://reuse")
                .Process(e => received = e.In.Body);
        });

        await ctxB.Start();

        _registry.GetProcessor("direct-vm://reuse").Should().NotBeNull();
    }
}

/// <summary>
/// Tests for Vm (cross-context asynchronous) component.
/// </summary>
public class VmComponentTests : IAsyncDisposable
{
    private readonly SharedVmRegistry _registry = new();
    private readonly ServiceProvider _sp;

    public VmComponentTests()
    {
        var sc = new ServiceCollection();
        sc.AddSingleton(_registry);
        _sp = sc.BuildServiceProvider();
    }

    public async ValueTask DisposeAsync()
    {
        _sp.Dispose();
        GC.SuppressFinalize(this);
    }

    private RouteContext CreateContext(string id) => new(_sp, id);

    [Fact]
    public void Component_HasCorrectScheme()
    {
        var component = new VmComponent();
        component.Scheme.Should().Be("vm");
    }

    [Fact]
    public void CreateEndpoint_ReturnsVmEndpoint()
    {
        var component = new VmComponent();
        var uri = EndpointUriParser.Parse("vm://myqueue");
        var endpoint = component.CreateEndpoint(uri);
        endpoint.Should().BeOfType<VmEndpoint>();
    }

    [Fact]
    public void Options_DefaultValues()
    {
        var opts = new VmEndpointOptions();
        opts.ConcurrentConsumers.Should().Be(1);
        opts.Size.Should().Be(0);
    }

    [Fact]
    public void Options_Validate_ThrowsOnInvalidConcurrentConsumers()
    {
        var opts = new VmEndpointOptions { ConcurrentConsumers = 0 };
        var act = () => opts.Validate();
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Options_Validate_ThrowsOnNegativeSize()
    {
        var opts = new VmEndpointOptions { Size = -1 };
        var act = () => opts.Validate();
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task CrossContext_AsyncDelivery()
    {
        var received = new ConcurrentBag<object?>();

        await using var ctxA = CreateContext("vm-ctx-a");
        await using var ctxB = CreateContext("vm-ctx-b");

        // Context A: consumer on vm://shared-queue
        ctxA.AddRoutes(r =>
        {
            r.From("vm://shared-queue")
                .Process(e => received.Add(e.In.Body));
        });

        // Context B: producer sends to vm://shared-queue
        ctxB.AddRoutes(r =>
        {
            r.From("direct://trigger")
                .To("vm://shared-queue");
        });

        await ctxA.Start();
        await ctxB.Start();

        var producer = ctxB.GetEndpoint("direct://trigger").CreateProducer();
        await producer.Start();

        await producer.Process(new Exchange(new Message { Body = "msg1" }));
        await producer.Process(new Exchange(new Message { Body = "msg2" }));

        await WaitForCondition(() => received.Count >= 2, TimeSpan.FromSeconds(5));

        received.Should().HaveCount(2);
        received.Should().Contain("msg1");
        received.Should().Contain("msg2");
    }

    [Fact]
    public async Task SameContext_AsyncDelivery()
    {
        var received = new ConcurrentBag<object?>();

        await using var ctx = CreateContext("vm-single");

        ctx.AddRoutes(r =>
        {
            r.From("vm://local-queue")
                .Process(e => received.Add(e.In.Body));
        });

        ctx.AddRoutes(r =>
        {
            r.From("direct://input")
                .To("vm://local-queue");
        });

        await ctx.Start();

        var producer = ctx.GetEndpoint("direct://input").CreateProducer();
        await producer.Start();

        await producer.Process(new Exchange(new Message { Body = "local1" }));
        await producer.Process(new Exchange(new Message { Body = "local2" }));

        await WaitForCondition(() => received.Count >= 2, TimeSpan.FromSeconds(5));

        received.Should().HaveCount(2);
    }

    [Fact]
    public async Task ConcurrentConsumers_ProcessInParallel()
    {
        var processed = new ConcurrentBag<string>();

        await using var ctxA = CreateContext("vm-parallel-a");
        await using var ctxB = CreateContext("vm-parallel-b");

        ctxA.AddRoutes(r =>
        {
            r.From("vm://parallel-queue?concurrentConsumers=3")
                .Process(async (e, ct) =>
                {
                    await Task.Delay(50, ct);
                    processed.Add(e.In.Body!.ToString()!);
                });
        });

        ctxB.AddRoutes(r =>
        {
            r.From("direct://parallel-input")
                .To("vm://parallel-queue");
        });

        await ctxA.Start();
        await ctxB.Start();

        var producer = ctxB.GetEndpoint("direct://parallel-input").CreateProducer();
        await producer.Start();

        for (int i = 0; i < 6; i++)
            await producer.Process(new Exchange(new Message { Body = $"msg-{i}" }));

        await WaitForCondition(() => processed.Count >= 6, TimeSpan.FromSeconds(5));

        processed.Should().HaveCount(6);
    }

    [Fact]
    public async Task Stop_GracefulShutdown()
    {
        await using var ctx = CreateContext("vm-graceful");

        ctx.AddRoutes(r =>
        {
            r.From("vm://graceful")
                .Process(_ => { });
        });

        await ctx.Start();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await ctx.Stop(cts.Token);
    }

    [Fact]
    public async Task MultipleContextConsumers_ShareChannel()
    {
        // Both contexts consume from same vm channel — messages distributed between them
        var receivedA = new ConcurrentBag<string>();
        var receivedB = new ConcurrentBag<string>();

        await using var ctxConsumerA = CreateContext("vm-multi-a");
        await using var ctxConsumerB = CreateContext("vm-multi-b");
        await using var ctxProducer = CreateContext("vm-multi-prod");

        ctxConsumerA.AddRoutes(r =>
        {
            r.From("vm://multi-queue")
                .Process(e => receivedA.Add(e.In.Body!.ToString()!));
        });

        ctxConsumerB.AddRoutes(r =>
        {
            r.From("vm://multi-queue")
                .Process(e => receivedB.Add(e.In.Body!.ToString()!));
        });

        ctxProducer.AddRoutes(r =>
        {
            r.From("direct://multi-trigger")
                .To("vm://multi-queue");
        });

        await ctxConsumerA.Start();
        await ctxConsumerB.Start();
        await ctxProducer.Start();

        var producer = ctxProducer.GetEndpoint("direct://multi-trigger").CreateProducer();
        await producer.Start();

        for (int i = 0; i < 20; i++)
            await producer.Process(new Exchange(new Message { Body = $"m-{i}" }));

        await WaitForCondition(() => receivedA.Count + receivedB.Count >= 20, TimeSpan.FromSeconds(5));

        // All messages should be processed (some by A, some by B)
        (receivedA.Count + receivedB.Count).Should().Be(20);
    }

    private static async Task WaitForCondition(Func<bool> condition, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        while (!condition())
        {
            cts.Token.ThrowIfCancellationRequested();
            await Task.Delay(10, cts.Token);
        }
    }
}

/// <summary>
/// Tests for the SharedVmRegistry itself.
/// </summary>
public class SharedVmRegistryTests
{
    [Fact]
    public void RegisterAndGetProcessor()
    {
        var registry = new SharedVmRegistry();
        var processor = Substitute.For<IProcessor>();

        registry.TryRegisterProcessor("test", processor).Should().BeTrue();
        registry.GetProcessor("test").Should().BeSameAs(processor);
    }

    [Fact]
    public void DuplicateRegister_ReturnsFalse()
    {
        var registry = new SharedVmRegistry();
        var p1 = Substitute.For<IProcessor>();
        var p2 = Substitute.For<IProcessor>();

        registry.TryRegisterProcessor("test", p1).Should().BeTrue();
        registry.TryRegisterProcessor("test", p2).Should().BeFalse();
    }

    [Fact]
    public void Unregister_RemovesProcessor()
    {
        var registry = new SharedVmRegistry();
        var processor = Substitute.For<IProcessor>();

        registry.TryRegisterProcessor("test", processor);
        registry.TryUnregisterProcessor("test", processor).Should().BeTrue();
        registry.GetProcessor("test").Should().BeNull();
    }

    [Fact]
    public void Unregister_WrongInstance_ReturnsFalse()
    {
        var registry = new SharedVmRegistry();
        var p1 = Substitute.For<IProcessor>();
        var p2 = Substitute.For<IProcessor>();

        registry.TryRegisterProcessor("test", p1);
        registry.TryUnregisterProcessor("test", p2).Should().BeFalse();
        registry.GetProcessor("test").Should().BeSameAs(p1);
    }

    [Fact]
    public void GetOrCreateChannel_ReturnsSameChannel()
    {
        var registry = new SharedVmRegistry();
        var ch1 = registry.GetOrCreateChannel("q1");
        var ch2 = registry.GetOrCreateChannel("q1");
        ch1.Should().BeSameAs(ch2);
    }

    [Fact]
    public void TryRemoveChannel_Removes()
    {
        var registry = new SharedVmRegistry();
        registry.GetOrCreateChannel("q1");
        registry.TryRemoveChannel("q1", out var ch).Should().BeTrue();
        ch.Should().NotBeNull();

        // After removal, a new call creates a new channel
        var ch2 = registry.GetOrCreateChannel("q1");
        ch2.Should().NotBeSameAs(ch);
    }
}

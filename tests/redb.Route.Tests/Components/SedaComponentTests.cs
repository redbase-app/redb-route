using FluentAssertions;
using redb.Route.Abstractions;
using redb.Route.Components;
using redb.Route.Core;

namespace redb.Route.Tests.Components;

/// <summary>
/// Tests for the SEDA (Staged Event-Driven Architecture) component.
/// </summary>
public class SedaComponentTests : IAsyncDisposable
{
    private readonly RouteContext _context = new();

    public async ValueTask DisposeAsync()
    {
        await _context.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Component_HasCorrectScheme()
    {
        var component = new SedaComponent();
        component.Scheme.Should().Be("seda");
    }

    [Fact]
    public void CreateEndpoint_ReturnsSedaEndpoint()
    {
        var component = new SedaComponent();
        var uri = EndpointUriParser.Parse("seda://myqueue");
        var endpoint = component.CreateEndpoint(uri);
        endpoint.Should().BeOfType<SedaEndpoint>();
    }

    [Fact]
    public void CreateEndpoint_SameUri_ReturnsSameInstance()
    {
        var component = new SedaComponent();
        var uri1 = EndpointUriParser.Parse("seda://myqueue");
        var uri2 = EndpointUriParser.Parse("seda://myqueue");
        var ep1 = component.CreateEndpoint(uri1);
        var ep2 = component.CreateEndpoint(uri2);
        ep1.Should().BeSameAs(ep2);
    }

    [Fact]
    public void Endpoint_Unbounded_ReturnsNegativeOneForQueueSize()
    {
        var component = new SedaComponent();
        var uri = EndpointUriParser.Parse("seda://test");
        var endpoint = (SedaEndpoint)component.CreateEndpoint(uri);
        // Unbounded channels don't support Count, so CurrentQueueSize returns -1
        endpoint.CurrentQueueSize.Should().Be(-1);
    }

    [Fact]
    public void Options_DefaultValues()
    {
        var opts = new SedaEndpointOptions();
        opts.ConcurrentConsumers.Should().Be(1);
        opts.Size.Should().Be(0);
        opts.Timeout.Should().Be(30000);
    }

    [Fact]
    public void Options_Validate_ThrowsOnInvalidConcurrentConsumers()
    {
        var opts = new SedaEndpointOptions { ConcurrentConsumers = 0 };
        var act = () => opts.Validate();
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Options_Validate_ThrowsOnNegativeSize()
    {
        var opts = new SedaEndpointOptions { Size = -1 };
        var act = () => opts.Validate();
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task Seda_AsyncDelivery_ProducerReturnsImmediately()
    {
        var received = new List<object?>();

        _context.AddRoutes(r =>
        {
            r.From("direct://input")
                .To("seda://queue1");
        });

        _context.AddRoutes(r =>
        {
            r.From("seda://queue1")
                .Process(e => received.Add(e.In.Body));
        });

        await _context.Start();

        var producer = _context.GetEndpoint("direct://input").CreateProducer();
        await producer.Start();

        await producer.Process(new Exchange(new Message { Body = "msg1" }));
        await producer.Process(new Exchange(new Message { Body = "msg2" }));

        // Give the async consumer a moment to process
        await WaitForCondition(() => received.Count >= 2, TimeSpan.FromSeconds(5));

        received.Should().HaveCount(2);
        received.Should().Contain("msg1");
        received.Should().Contain("msg2");
    }

    [Fact]
    public async Task Seda_ConcurrentConsumers_ProcessInParallel()
    {
        var processed = new System.Collections.Concurrent.ConcurrentBag<string>();

        _context.AddRoutes(r =>
        {
            r.From("direct://parallel-input")
                .To("seda://parallel-queue?concurrentConsumers=3");
        });

        _context.AddRoutes(r =>
        {
            r.From("seda://parallel-queue?concurrentConsumers=3")
                .Process(async (e, ct) =>
                {
                    await Task.Delay(50, ct);
                    processed.Add(e.In.Body!.ToString()!);
                });
        });

        await _context.Start();

        var producer = _context.GetEndpoint("direct://parallel-input").CreateProducer();
        await producer.Start();

        // Send 6 messages
        for (int i = 0; i < 6; i++)
            await producer.Process(new Exchange(new Message { Body = $"msg-{i}" }));

        await WaitForCondition(() => processed.Count >= 6, TimeSpan.FromSeconds(5));

        processed.Should().HaveCount(6);
    }

    [Fact]
    public void Seda_BoundedQueue_CreatesBoundedEndpoint()
    {
        var component = new SedaComponent();
        var uri = EndpointUriParser.Parse("seda://bounded?size=5");
        var endpoint = (SedaEndpoint)component.CreateEndpoint(uri);

        endpoint.CurrentQueueSize.Should().Be(0);
    }

    [Fact]
    public async Task Seda_ProcessedCount_Increments()
    {
        var received = new List<object?>();

        _context.AddRoutes(r =>
        {
            r.From("direct://count-input")
                .To("seda://count-queue");
        });

        _context.AddRoutes(r =>
        {
            r.From("seda://count-queue")
                .Process(e => received.Add(e.In.Body));
        });

        await _context.Start();

        var producer = _context.GetEndpoint("direct://count-input").CreateProducer();
        await producer.Start();

        await producer.Process(new Exchange(new Message { Body = "a" }));
        await producer.Process(new Exchange(new Message { Body = "b" }));
        await producer.Process(new Exchange(new Message { Body = "c" }));

        await WaitForCondition(() => received.Count >= 3, TimeSpan.FromSeconds(5));

        received.Should().HaveCount(3);
    }

    [Fact]
    public async Task Seda_StopConsumer_GracefulShutdown()
    {
        _context.AddRoutes(r =>
        {
            r.From("seda://graceful")
                .Process(_ => { });
        });

        await _context.Start();

        // Should stop without hanging
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await _context.Stop(cts.Token);
    }

    [Fact]
    public async Task Seda_OrderPreservation_SingleConsumer()
    {
        var received = new List<int>();

        _context.AddRoutes(r =>
        {
            r.From("direct://order-in")
                .To("seda://order-queue");
        });

        _context.AddRoutes(r =>
        {
            r.From("seda://order-queue")
                .Process(e => received.Add((int)e.In.Body!));
        });

        await _context.Start();

        var producer = _context.GetEndpoint("direct://order-in").CreateProducer();
        await producer.Start();

        for (int i = 0; i < 10; i++)
            await producer.Process(new Exchange(new Message { Body = i }));

        await WaitForCondition(() => received.Count >= 10, TimeSpan.FromSeconds(5));

        // With a single consumer, order should be preserved
        received.Should().BeInAscendingOrder();
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

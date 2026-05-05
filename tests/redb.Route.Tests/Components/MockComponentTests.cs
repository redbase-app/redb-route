using FluentAssertions;
using redb.Route.Abstractions;
using redb.Route.Components;
using redb.Route.Core;

namespace redb.Route.Tests.Components;

/// <summary>
/// Tests for the Mock component.
/// </summary>
public class MockComponentTests : IAsyncDisposable
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
        var component = new MockComponent();
        component.Scheme.Should().Be("mock");
    }

    [Fact]
    public void CreateEndpoint_ReturnsMockEndpoint()
    {
        var component = new MockComponent();
        var uri = EndpointUriParser.Parse("mock://result");
        var endpoint = component.CreateEndpoint(uri);
        endpoint.Should().BeOfType<MockEndpoint>();
    }

    [Fact]
    public void CreateEndpoint_SameUri_ReturnsSameInstance()
    {
        var component = new MockComponent();
        var uri1 = EndpointUriParser.Parse("mock://abc");
        var uri2 = EndpointUriParser.Parse("mock://abc");
        component.CreateEndpoint(uri1).Should().BeSameAs(component.CreateEndpoint(uri2));
    }

    [Fact]
    public void GetEndpoint_ReturnsExistingEndpoint()
    {
        var component = new MockComponent();
        var uri = EndpointUriParser.Parse("mock://result");
        var created = component.CreateEndpoint(uri);
        var found = component.GetEndpoint("result");
        found.Should().BeSameAs(created);
    }

    [Fact]
    public void GetEndpoint_ReturnsNull_WhenNotFound()
    {
        var component = new MockComponent();
        component.GetEndpoint("missing").Should().BeNull();
    }

    [Fact]
    public void MockEndpoint_InitialState_Empty()
    {
        var component = new MockComponent();
        var ep = (MockEndpoint)component.CreateEndpoint(EndpointUriParser.Parse("mock://test"));
        ep.ReceivedCount.Should().Be(0);
        ep.ReceivedExchanges.Should().BeEmpty();
    }

    [Fact]
    public void MockEndpoint_Options_DefaultExpectedCount()
    {
        var opts = new MockEndpointOptions();
        opts.ExpectedMessageCount.Should().Be(0);
    }

    [Fact]
    public async Task MockProducer_CapturesExchange()
    {
        var component = new MockComponent();
        var ep = (MockEndpoint)component.CreateEndpoint(EndpointUriParser.Parse("mock://capture"));
        var producer = ep.CreateProducer();
        await producer.Start();

        var exchange = new Exchange(new Message { Body = "hello" });
        await producer.Process(exchange);

        ep.ReceivedCount.Should().Be(1);
        ep.ReceivedExchanges[0].In.Body.Should().Be("hello");
    }

    [Fact]
    public async Task MockProducer_CapturesMultipleExchanges()
    {
        var component = new MockComponent();
        var ep = (MockEndpoint)component.CreateEndpoint(EndpointUriParser.Parse("mock://multi"));
        var producer = ep.CreateProducer();
        await producer.Start();

        for (int i = 0; i < 5; i++)
            await producer.Process(new Exchange(new Message { Body = $"msg-{i}" }));

        ep.ReceivedCount.Should().Be(5);
    }

    [Fact]
    public async Task Mock_InRoute_CapturesDeliveredExchanges()
    {
        _context.AddRoutes(r =>
        {
            r.From("direct://source")
                .SetHeader("X-Test", "value")
                .To("mock://result");
        });

        await _context.Start();

        var mockEndpoint = (MockEndpoint)_context.GetEndpoint("mock://result");

        var producer = _context.GetEndpoint("direct://source").CreateProducer();
        await producer.Start();

        await producer.Process(new Exchange(new Message { Body = "test-body" }));

        mockEndpoint.ReceivedCount.Should().Be(1);
        mockEndpoint.ReceivedExchanges[0].In.Body.Should().Be("test-body");
        mockEndpoint.ReceivedExchanges[0].In.Headers["X-Test"].Should().Be("value");
    }

    [Fact]
    public async Task Mock_WaitForSatisfied_ReturnsTrue_WhenCountReached()
    {
        var component = new MockComponent();
        var ep = (MockEndpoint)component.CreateEndpoint(
            EndpointUriParser.Parse("mock://wait?expectedMessageCount=3"));
        var producer = ep.CreateProducer();
        await producer.Start();

        ep.ExpectedMessageCount.Should().Be(3);

        // Send 3 messages
        for (int i = 0; i < 3; i++)
            await producer.Process(new Exchange(new Message { Body = i }));

        var satisfied = await ep.WaitForSatisfied(TimeSpan.FromSeconds(5));
        satisfied.Should().BeTrue();
    }

    [Fact]
    public async Task Mock_WaitForSatisfied_ReturnsFalse_OnTimeout()
    {
        var component = new MockComponent();
        var ep = (MockEndpoint)component.CreateEndpoint(
            EndpointUriParser.Parse("mock://timeout?expectedMessageCount=10"));
        var producer = ep.CreateProducer();
        await producer.Start();

        // Only send 1 message (expected 10)
        await producer.Process(new Exchange(new Message { Body = "one" }));

        var satisfied = await ep.WaitForSatisfied(TimeSpan.FromMilliseconds(100));
        satisfied.Should().BeFalse();
    }

    [Fact]
    public async Task Mock_WaitForSatisfied_NoExpectation_ReturnsTrue()
    {
        var component = new MockComponent();
        var ep = (MockEndpoint)component.CreateEndpoint(EndpointUriParser.Parse("mock://noexpect"));

        var satisfied = await ep.WaitForSatisfied(TimeSpan.FromSeconds(1));
        satisfied.Should().BeTrue();
    }

    [Fact]
    public async Task Mock_Reset_ClearsReceivedExchanges()
    {
        var component = new MockComponent();
        var ep = (MockEndpoint)component.CreateEndpoint(EndpointUriParser.Parse("mock://reset"));
        var producer = ep.CreateProducer();
        await producer.Start();

        await producer.Process(new Exchange(new Message { Body = "a" }));
        await producer.Process(new Exchange(new Message { Body = "b" }));

        ep.ReceivedCount.Should().Be(2);

        ep.Reset();

        ep.ReceivedCount.Should().Be(0);
        ep.ReceivedExchanges.Should().BeEmpty();
    }

    [Fact]
    public async Task Mock_MultipleRoutes_CapturesSeparately()
    {
        _context.AddRoutes(r =>
        {
            r.From("direct://a")
                .To("mock://result-a");
        });

        _context.AddRoutes(r =>
        {
            r.From("direct://b")
                .To("mock://result-b");
        });

        await _context.Start();

        var mockA = (MockEndpoint)_context.GetEndpoint("mock://result-a");
        var mockB = (MockEndpoint)_context.GetEndpoint("mock://result-b");

        var producerA = _context.GetEndpoint("direct://a").CreateProducer();
        await producerA.Start();
        var producerB = _context.GetEndpoint("direct://b").CreateProducer();
        await producerB.Start();

        await producerA.Process(new Exchange(new Message { Body = "from-a" }));
        await producerB.Process(new Exchange(new Message { Body = "from-b-1" }));
        await producerB.Process(new Exchange(new Message { Body = "from-b-2" }));

        mockA.ReceivedCount.Should().Be(1);
        mockB.ReceivedCount.Should().Be(2);
    }
}

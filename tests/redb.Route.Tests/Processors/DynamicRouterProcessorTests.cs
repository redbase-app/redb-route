using FluentAssertions;
using NSubstitute;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Processors;

namespace redb.Route.Tests.Processors;

/// <summary>Tests for <see cref="DynamicRouterProcessor"/>.</summary>
public class DynamicRouterProcessorTests
{
    private static (IRouteContext context, Dictionary<string, List<IExchange>> received) SetupContext(
        params string[] uris)
    {
        var received = new Dictionary<string, List<IExchange>>();
        var context = Substitute.For<IRouteContext>();

        foreach (var uri in uris)
        {
            received[uri] = [];
            var capturedUri = uri;
            var producer = Substitute.For<IProducer>();
            producer.When(p => p.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>()))
                .Do(ci => received[capturedUri].Add(ci.Arg<IExchange>()));
            var endpoint = Substitute.For<IEndpoint>();
            endpoint.CreateProducer().Returns(producer);
            context.GetEndpoint(uri).Returns(endpoint);
        }

        return (context, received);
    }

    [Fact]
    public async Task Process_SingleHop_RoutesToUri()
    {
        var (context, received) = SetupContext("direct://step1");
        var callCount = 0;
        var router = new DynamicRouterProcessor(context, _ =>
        {
            callCount++;
            return callCount == 1 ? "direct://step1" : null;
        });

        await router.Process(new Exchange(new Message("data")));

        received["direct://step1"].Should().HaveCount(1);
    }

    [Fact]
    public async Task Process_MultipleHops_VisitsEachUri()
    {
        var (context, received) = SetupContext("direct://a", "direct://b", "direct://c");
        var hops = new Queue<string?>(["direct://a", "direct://b", "direct://c", null]);
        var router = new DynamicRouterProcessor(context, _ => hops.Dequeue());

        await router.Process(new Exchange(new Message("slip")));

        received["direct://a"].Should().HaveCount(1);
        received["direct://b"].Should().HaveCount(1);
        received["direct://c"].Should().HaveCount(1);
    }

    [Fact]
    public async Task Process_NullOnFirstCall_DoesNothing()
    {
        var context = Substitute.For<IRouteContext>();
        var router = new DynamicRouterProcessor(context, _ => null);

        await router.Process(new Exchange());

        context.DidNotReceive().GetEndpoint(Arg.Any<string>());
    }

    [Fact]
    public async Task Process_RoutingFunctionSeesModifiedExchange()
    {
        var bodies = new List<object?>();
        var producer = Substitute.For<IProducer>();
        producer.When(p => p.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>()))
            .Do(ci => ci.Arg<IExchange>().In.Body = "modified");
        var endpoint = Substitute.For<IEndpoint>();
        endpoint.CreateProducer().Returns(producer);
        var context = Substitute.For<IRouteContext>();
        context.GetEndpoint(Arg.Any<string>()).Returns(endpoint);

        var callCount = 0;
        var router = new DynamicRouterProcessor(context, ex =>
        {
            bodies.Add(ex.In.Body);
            callCount++;
            return callCount <= 2 ? "direct://step" : null;
        });

        await router.Process(new Exchange(new Message("original")));

        bodies[0].Should().Be("original");
        bodies[1].Should().Be("modified"); // After first hop modified the body
    }

    [Fact]
    public async Task Process_ExceedsMaxHops_ThrowsInvalidOperation()
    {
        var producer = Substitute.For<IProducer>();
        var endpoint = Substitute.For<IEndpoint>();
        endpoint.CreateProducer().Returns(producer);
        var context = Substitute.For<IRouteContext>();
        context.GetEndpoint(Arg.Any<string>()).Returns(endpoint);

        // Always returns a URI → infinite loop
        var router = new DynamicRouterProcessor(context, _ => "direct://infinite");

        var act = () => router.Process(new Exchange());
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*maximum hop count*");
    }

    [Fact]
    public async Task Process_StoppedExchange_BreaksLoop()
    {
        var (context, received) = SetupContext("direct://a", "direct://b");
        // Producer for "a" stops the exchange
        var producerA = Substitute.For<IProducer>();
        producerA.When(p => p.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>()))
            .Do(ci => ci.Arg<IExchange>().Stop());
        var endpointA = Substitute.For<IEndpoint>();
        endpointA.CreateProducer().Returns(producerA);
        context.GetEndpoint("direct://a").Returns(endpointA);

        var hops = new Queue<string?>(["direct://a", "direct://b", null]);
        var router = new DynamicRouterProcessor(context, _ => hops.Dequeue());

        await router.Process(new Exchange());

        // "b" should not be reached because exchange was stopped after "a"
        received["direct://b"].Should().BeEmpty();
    }

    [Fact]
    public async Task Process_CachesProducerPerUri()
    {
        var producer = Substitute.For<IProducer>();
        var endpoint = Substitute.For<IEndpoint>();
        endpoint.CreateProducer().Returns(producer);
        var context = Substitute.For<IRouteContext>();
        context.GetEndpoint("direct://x").Returns(endpoint);

        var callCount = 0;
        var router = new DynamicRouterProcessor(context, _ =>
            ++callCount <= 2 ? "direct://x" : null); // 2 hops to same URI

        await router.Process(new Exchange());

        // Should create producer only once despite 2 hops
        endpoint.Received(1).CreateProducer();
    }

    [Fact]
    public void Constructor_NullContext_Throws()
    {
        var act = () => new DynamicRouterProcessor(null!, _ => null);
        act.Should().Throw<ArgumentNullException>().WithParameterName("context");
    }

    [Fact]
    public void Constructor_NullRoutingFunction_Throws()
    {
        var act = () => new DynamicRouterProcessor(Substitute.For<IRouteContext>(), null!);
        act.Should().Throw<ArgumentNullException>().WithParameterName("routingFunction");
    }

    [Fact]
    public async Task Process_Cancellation_StopsRouting()
    {
        var producer = Substitute.For<IProducer>();
        var endpoint = Substitute.For<IEndpoint>();
        endpoint.CreateProducer().Returns(producer);
        var context = Substitute.For<IRouteContext>();
        context.GetEndpoint(Arg.Any<string>()).Returns(endpoint);

        using var cts = new CancellationTokenSource();
        var callCount = 0;
        var router = new DynamicRouterProcessor(context, _ =>
        {
            callCount++;
            if (callCount == 2) cts.Cancel();
            return "direct://loop";
        });

        // Should stop after cancellation
        await router.Process(new Exchange(), cts.Token);

        callCount.Should().Be(2); // Called twice, cancelled before 3rd
    }
}

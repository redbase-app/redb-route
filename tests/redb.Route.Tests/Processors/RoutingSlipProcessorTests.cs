using FluentAssertions;
using NSubstitute;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Processors;

namespace redb.Route.Tests.Processors;

/// <summary>Tests for <see cref="RoutingSlipProcessor"/>.</summary>
public class RoutingSlipProcessorTests
{
    private static (IRouteContext context, List<string> visitOrder) SetupContext(params string[] uris)
    {
        var visitOrder = new List<string>();
        var context = Substitute.For<IRouteContext>();

        foreach (var uri in uris)
        {
            var capturedUri = uri;
            var producer = Substitute.For<IProducer>();
            producer.When(p => p.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>()))
                .Do(_ => visitOrder.Add(capturedUri));
            var endpoint = Substitute.For<IEndpoint>();
            endpoint.CreateProducer().Returns(producer);
            context.GetEndpoint(uri).Returns(endpoint);
        }

        return (context, visitOrder);
    }

    [Fact]
    public async Task Process_VisitsEachEndpoint_InSlipOrder()
    {
        var (context, order) = SetupContext("direct://a", "direct://b", "direct://c");
        var slip = new RoutingSlipProcessor(context, _ => new[] { "direct://a", "direct://b", "direct://c" });

        await slip.Process(new Exchange(new Message("data")));

        order.Should().Equal("direct://a", "direct://b", "direct://c");
    }

    [Fact]
    public async Task Process_SlipComputedOnce_NotPerHop()
    {
        // The defining property of Routing Slip vs Dynamic Router: the slip factory runs ONCE.
        var (context, _) = SetupContext("direct://a", "direct://b", "direct://c");
        var factoryCalls = 0;
        var slip = new RoutingSlipProcessor(context, _ =>
        {
            factoryCalls++;
            return new[] { "direct://a", "direct://b", "direct://c" };
        });

        await slip.Process(new Exchange(new Message("data")));

        factoryCalls.Should().Be(1);
    }

    [Fact]
    public async Task Process_PipelinesOutToIn_BetweenHops()
    {
        var seenByB = new List<object?>();
        var context = Substitute.For<IRouteContext>();

        // "a" produces an Out; the next hop must see it as In.
        var producerA = Substitute.For<IProducer>();
        producerA.When(p => p.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>()))
            .Do(ci => ci.Arg<IExchange>().Out = new Message("from-a"));
        var endpointA = Substitute.For<IEndpoint>();
        endpointA.CreateProducer().Returns(producerA);
        context.GetEndpoint("direct://a").Returns(endpointA);

        var producerB = Substitute.For<IProducer>();
        producerB.When(p => p.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>()))
            .Do(ci => seenByB.Add(ci.Arg<IExchange>().In.Body));
        var endpointB = Substitute.For<IEndpoint>();
        endpointB.CreateProducer().Returns(producerB);
        context.GetEndpoint("direct://b").Returns(endpointB);

        var slip = new RoutingSlipProcessor(context, _ => new[] { "direct://a", "direct://b" });

        await slip.Process(new Exchange(new Message("original")));

        seenByB.Should().ContainSingle().Which.Should().Be("from-a");
    }

    [Fact]
    public async Task Process_FinalHopOut_LeftIntactForInOutCaller()
    {
        var context = Substitute.For<IRouteContext>();
        var producer = Substitute.For<IProducer>();
        producer.When(p => p.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>()))
            .Do(ci => ci.Arg<IExchange>().Out = new Message("reply"));
        var endpoint = Substitute.For<IEndpoint>();
        endpoint.CreateProducer().Returns(producer);
        context.GetEndpoint("direct://only").Returns(endpoint);

        var slip = new RoutingSlipProcessor(context, _ => new[] { "direct://only" });
        var exchange = new Exchange(new Message("q"));

        await slip.Process(exchange);

        exchange.HasOut.Should().BeTrue();
        exchange.Out!.Body.Should().Be("reply");
    }

    [Fact]
    public async Task Process_SetsSlipEndpointProperty()
    {
        var (context, _) = SetupContext("direct://a", "direct://b");
        var slip = new RoutingSlipProcessor(context, _ => new[] { "direct://a", "direct://b" });
        var exchange = new Exchange(new Message("data"));

        await slip.Process(exchange);

        // Property carries the last endpoint processed (Camel: CamelSlipEndpoint).
        exchange.Properties[RoutingSlipProcessor.SlipEndpointProperty].Should().Be("direct://b");
    }

    [Fact]
    public async Task Process_IgnoreInvalidEndpoints_SkipsAndContinues()
    {
        var (context, order) = SetupContext("direct://a", "direct://c");
        context.GetEndpoint("direct://bad").Returns(_ => throw new InvalidOperationException("no such endpoint"));

        var slip = new RoutingSlipProcessor(context,
            _ => new[] { "direct://a", "direct://bad", "direct://c" },
            ignoreInvalidEndpoints: true);

        await slip.Process(new Exchange(new Message("data")));

        order.Should().Equal("direct://a", "direct://c");
    }

    [Fact]
    public async Task Process_InvalidEndpoint_ThrowsWhenNotIgnored()
    {
        var context = Substitute.For<IRouteContext>();
        context.GetEndpoint("direct://bad").Returns(_ => throw new InvalidOperationException("no such endpoint"));

        var slip = new RoutingSlipProcessor(context, _ => new[] { "direct://bad" });

        var act = () => slip.Process(new Exchange());
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Process_StoppedExchange_BreaksSlip()
    {
        var (context, order) = SetupContext("direct://b");
        var producerA = Substitute.For<IProducer>();
        producerA.When(p => p.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>()))
            .Do(ci => ci.Arg<IExchange>().Stop());
        var endpointA = Substitute.For<IEndpoint>();
        endpointA.CreateProducer().Returns(producerA);
        context.GetEndpoint("direct://a").Returns(endpointA);

        var slip = new RoutingSlipProcessor(context, _ => new[] { "direct://a", "direct://b" });

        await slip.Process(new Exchange());

        order.Should().BeEmpty(); // "b" not reached after "a" stopped the exchange
    }

    [Fact]
    public async Task Process_EmptySlip_DoesNothing()
    {
        var context = Substitute.For<IRouteContext>();
        var slip = new RoutingSlipProcessor(context, _ => Array.Empty<string>());

        await slip.Process(new Exchange());

        context.DidNotReceive().GetEndpoint(Arg.Any<string>());
    }

    [Fact]
    public async Task Process_CachesProducerPerUri()
    {
        var producer = Substitute.For<IProducer>();
        var endpoint = Substitute.For<IEndpoint>();
        endpoint.CreateProducer().Returns(producer);
        var context = Substitute.For<IRouteContext>();
        context.GetEndpoint("direct://x").Returns(endpoint);

        // Same URI twice in one slip.
        var slip = new RoutingSlipProcessor(context, _ => new[] { "direct://x", "direct://x" });

        await slip.Process(new Exchange());

        endpoint.Received(1).CreateProducer();
    }

    [Fact]
    public void Constructor_NullContext_Throws()
    {
        var act = () => new RoutingSlipProcessor(null!, _ => Array.Empty<string>());
        act.Should().Throw<ArgumentNullException>().WithParameterName("context");
    }

    [Fact]
    public void Constructor_NullSlipFactory_Throws()
    {
        var act = () => new RoutingSlipProcessor(Substitute.For<IRouteContext>(), null!);
        act.Should().Throw<ArgumentNullException>().WithParameterName("slipFactory");
    }
}

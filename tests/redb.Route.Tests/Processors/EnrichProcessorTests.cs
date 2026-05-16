using FluentAssertions;
using NSubstitute;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Processors;

namespace redb.Route.Tests.Processors;

/// <summary>Tests for <see cref="EnrichProcessor"/>.</summary>
public class EnrichProcessorTests
{
    private static (IRouteContext context, IProducer producer) SetupContext(string uri)
    {
        var producer = Substitute.For<IProducer>();
        var endpoint = Substitute.For<IEndpoint>();
        endpoint.CreateProducer().Returns(producer);
        var context = Substitute.For<IRouteContext>();
        context.GetEndpoint(uri).Returns(endpoint);
        return (context, producer);
    }

    [Fact]
    public async Task Process_MergesEnrichedData()
    {
        var (context, producer) = SetupContext("direct://lookup");
        producer.When(p => p.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>()))
            .Do(ci =>
            {
                var ex = ci.Arg<IExchange>();
                ex.In.Headers["enriched"] = "yes";
                ex.In.Body = "enriched-body";
            });

        var enricher = new EnrichProcessor(context, "direct://lookup",
            mergeStrategy: (original, enriched) =>
            {
                original.In.Headers["enriched"] = enriched.In.Headers["enriched"];
                original.In.Body = $"{original.In.Body}+{enriched.In.Body}";
                return original;
            });

        var exchange = new Exchange(new Message("original-body"));
        await enricher.Process(exchange);

        exchange.In.Body.Should().Be("original-body+enriched-body");
        exchange.In.Headers["enriched"].Should().Be("yes");
    }

    [Fact]
    public async Task Process_SendsCloneToResource()
    {
        IExchange? sentExchange = null;
        var (context, producer) = SetupContext("direct://resource");
        producer.When(p => p.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>()))
            .Do(ci => sentExchange = ci.Arg<IExchange>());

        var enricher = new EnrichProcessor(context, "direct://resource",
            mergeStrategy: (orig, _) => orig);

        var original = new Exchange(new Message("data"));
        await enricher.Process(original);

        sentExchange.Should().NotBeNull();
        sentExchange.Should().NotBeSameAs(original);
        sentExchange!.In.Body.Should().Be("data");
        sentExchange.Pattern.Should().Be(ExchangePattern.InOut);
    }

    [Fact]
    public async Task Process_CachesProducer()
    {
        var (context, producer) = SetupContext("direct://resource");
        var enricher = new EnrichProcessor(context, "direct://resource",
            mergeStrategy: (orig, _) => orig);

        await enricher.Process(new Exchange(new Message("data")));
        await enricher.Process(new Exchange(new Message("data2")));

        // Producer created once, reused across calls
        context.GetEndpoint("direct://resource")
            .Received(1).CreateProducer();
    }

    [Fact]
    public async Task Process_PreservesOriginalWhenMergeReturnsOriginal()
    {
        var (context, producer) = SetupContext("direct://resource");
        producer.When(p => p.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>()))
            .Do(ci => ci.Arg<IExchange>().In.Body = "enriched");

        var enricher = new EnrichProcessor(context, "direct://resource",
            mergeStrategy: (orig, _) => orig); // Ignore enrichment

        var exchange = new Exchange(new Message("keep-me"));
        await enricher.Process(exchange);

        exchange.In.Body.Should().Be("keep-me");
    }

    [Fact]
    public void Constructor_NullContext_Throws()
    {
        var act = () => new EnrichProcessor(null!, "direct://a", (a, b) => a);
        act.Should().Throw<ArgumentNullException>().WithParameterName("context");
    }

    [Fact]
    public void Constructor_NullUri_Throws()
    {
        var act = () => new EnrichProcessor(Substitute.For<IRouteContext>(), null!, (a, b) => a);
        act.Should().Throw<ArgumentNullException>().WithParameterName("resourceUri");
    }

    [Fact]
    public void Constructor_NullMerge_Throws()
    {
        var act = () => new EnrichProcessor(Substitute.For<IRouteContext>(), "direct://a", null!);
        act.Should().Throw<ArgumentNullException>().WithParameterName("mergeStrategy");
    }
}

/// <summary>Tests for <see cref="PollEnrichProcessor"/>.</summary>
public class PollEnrichProcessorTests
{
    private static (IRouteContext context, IProducer producer) SetupContext(string uri)
    {
        var producer = Substitute.For<IProducer>();
        var endpoint = Substitute.For<IEndpoint>();
        endpoint.CreateProducer().Returns(producer);
        var context = Substitute.For<IRouteContext>();
        context.GetEndpoint(uri).Returns(endpoint);
        return (context, producer);
    }

    [Fact]
    public async Task Process_SuccessfulPoll_MergesData()
    {
        var (context, producer) = SetupContext("seda://queue");
        producer.When(p => p.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>()))
            .Do(ci => ci.Arg<IExchange>().In.Body = "polled-data");

        var pollEnricher = new PollEnrichProcessor(context, "seda://queue",
            mergeStrategy: (original, polled) =>
            {
                original.In.Body = $"{original.In.Body}+{polled!.In.Body}";
                return original;
            },
            timeout: TimeSpan.FromSeconds(5));

        var exchange = new Exchange(new Message("base"));
        await pollEnricher.Process(exchange);

        exchange.In.Body.Should().Be("base+polled-data");
    }

    [Fact]
    public async Task Process_Timeout_MergeReceivesNull()
    {
        var producer = Substitute.For<IProducer>();
        // NSubstitute's When..Do with async void doesn't delay the Task return;
        // use Returns with an actual async task instead.
        producer.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(async ci =>
            {
                var ct = ci.Arg<CancellationToken>();
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            });
        var endpoint = Substitute.For<IEndpoint>();
        endpoint.CreateProducer().Returns(producer);
        var context = Substitute.For<IRouteContext>();
        context.GetEndpoint("seda://slow").Returns(endpoint);

        var pollEnricher = new PollEnrichProcessor(context, "seda://slow",
            mergeStrategy: (original, polled) =>
            {
                original.In.Body = polled is null ? "timeout" : "got-data";
                return original;
            },
            timeout: TimeSpan.FromMilliseconds(50));

        var exchange = new Exchange(new Message("base"));
        await pollEnricher.Process(exchange);

        exchange.In.Body.Should().Be("timeout");
    }

    [Fact]
    public async Task Process_SetsInOutPattern()
    {
        IExchange? sent = null;
        var (context, producer) = SetupContext("seda://queue");
        producer.When(p => p.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>()))
            .Do(ci => sent = ci.Arg<IExchange>());

        var pollEnricher = new PollEnrichProcessor(context, "seda://queue",
            mergeStrategy: (orig, _) => orig);

        await pollEnricher.Process(new Exchange(new Message("data")));

        sent.Should().NotBeNull();
        sent!.Pattern.Should().Be(ExchangePattern.InOut);
    }

    [Fact]
    public void Constructor_NullContext_Throws()
    {
        var act = () => new PollEnrichProcessor(null!, "seda://a", (a, b) => a);
        act.Should().Throw<ArgumentNullException>().WithParameterName("context");
    }

    [Fact]
    public void Constructor_NullUri_Throws()
    {
        var act = () => new PollEnrichProcessor(Substitute.For<IRouteContext>(), null!, (a, b) => a);
        act.Should().Throw<ArgumentNullException>().WithParameterName("resourceUri");
    }

    [Fact]
    public void Constructor_NullMerge_Throws()
    {
        var act = () => new PollEnrichProcessor(Substitute.For<IRouteContext>(), "seda://a", null!);
        act.Should().Throw<ArgumentNullException>().WithParameterName("mergeStrategy");
    }
}

using FluentAssertions;
using NSubstitute;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Processors;

namespace redb.Route.Tests.Processors;

/// <summary>Tests for <see cref="RecipientListProcessor"/>.</summary>
public class RecipientListProcessorTests
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
    public async Task Process_Sequential_RoutesToAllRecipients()
    {
        var (context, received) = SetupContext("seda://a", "seda://b", "seda://c");
        var processor = new RecipientListProcessor(
            context,
            _ => new[] { "seda://a", "seda://b", "seda://c" });

        await processor.Process(new Exchange(new Message("data")));

        received["seda://a"].Should().HaveCount(1);
        received["seda://b"].Should().HaveCount(1);
        received["seda://c"].Should().HaveCount(1);
    }

    [Fact]
    public async Task Process_SendsClones_NotOriginal()
    {
        var capturedExchanges = new List<IExchange>();
        var producer = Substitute.For<IProducer>();
        producer.When(p => p.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>()))
            .Do(ci => capturedExchanges.Add(ci.Arg<IExchange>()));
        var endpoint = Substitute.For<IEndpoint>();
        endpoint.CreateProducer().Returns(producer);
        var context = Substitute.For<IRouteContext>();
        context.GetEndpoint("seda://a").Returns(endpoint);

        var processor = new RecipientListProcessor(
            context,
            _ => new[] { "seda://a" });

        var original = new Exchange(new Message("original"));
        await processor.Process(original);

        capturedExchanges.Should().HaveCount(1);
        capturedExchanges[0].Should().NotBeSameAs(original);
        capturedExchanges[0].In.Body.Should().Be("original");
    }

    [Fact]
    public async Task Process_DynamicUris_ResolvedAtRuntime()
    {
        var (context, received) = SetupContext("seda://x", "seda://y");
        var processor = new RecipientListProcessor(
            context,
            ex =>
            {
                var header = (string)ex.In.Headers["targets"]!;
                return header.Split(',');
            });

        var exchange = new Exchange(new Message("data"));
        exchange.In.Headers["targets"] = "seda://x,seda://y";
        await processor.Process(exchange);

        received["seda://x"].Should().HaveCount(1);
        received["seda://y"].Should().HaveCount(1);
    }

    [Fact]
    public async Task Process_EmptyList_DoesNothing()
    {
        var context = Substitute.For<IRouteContext>();
        var processor = new RecipientListProcessor(
            context,
            _ => Enumerable.Empty<string>());

        await processor.Process(new Exchange());
        // No exception, no calls
        context.DidNotReceive().GetEndpoint(Arg.Any<string>());
    }

    [Fact]
    public async Task Process_WithAggregation_MergesResults()
    {
        var producer = Substitute.For<IProducer>();
        producer.When(p => p.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>()))
            .Do(ci =>
            {
                var ex = ci.Arg<IExchange>();
                ex.In.Body = (int)ex.In.Body! * 10;
            });
        var endpoint = Substitute.For<IEndpoint>();
        endpoint.CreateProducer().Returns(producer);
        var context = Substitute.For<IRouteContext>();
        context.GetEndpoint(Arg.Any<string>()).Returns(endpoint);

        var processor = new RecipientListProcessor(
            context,
            _ => new[] { "seda://a", "seda://b" },
            aggregationStrategy: (a, b) =>
            {
                a.In.Body = (int)a.In.Body! + (int)b.In.Body!;
                return a;
            });

        var exchange = new Exchange(new Message(1));
        await processor.Process(exchange);

        // Each clone gets body=1, producer multiplies by 10 → 10 + 10 = 20
        exchange.In.Body.Should().Be(20);
    }

    [Fact]
    public async Task Process_Parallel_AllRecipientsReceive()
    {
        var (context, received) = SetupContext("seda://a", "seda://b");
        var processor = new RecipientListProcessor(
            context,
            _ => new[] { "seda://a", "seda://b" },
            parallelProcessing: true);

        await processor.Process(new Exchange(new Message("parallel")));

        received["seda://a"].Should().HaveCount(1);
        received["seda://b"].Should().HaveCount(1);
    }

    [Fact]
    public async Task Process_Parallel_WithAggregation()
    {
        var producer = Substitute.For<IProducer>();
        producer.When(p => p.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>()))
            .Do(ci => ci.Arg<IExchange>().In.Body = 5);
        var endpoint = Substitute.For<IEndpoint>();
        endpoint.CreateProducer().Returns(producer);
        var context = Substitute.For<IRouteContext>();
        context.GetEndpoint(Arg.Any<string>()).Returns(endpoint);

        var processor = new RecipientListProcessor(
            context,
            _ => new[] { "seda://a", "seda://b", "seda://c" },
            parallelProcessing: true,
            aggregationStrategy: (a, b) =>
            {
                a.In.Body = (int)a.In.Body! + (int)b.In.Body!;
                return a;
            });

        var exchange = new Exchange(new Message(0));
        await processor.Process(exchange);

        exchange.In.Body.Should().Be(15); // 5+5+5
    }

    [Fact]
    public async Task Process_StopOnException_Sequential_Propagates()
    {
        var producer = Substitute.For<IProducer>();
        producer.When(p => p.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>()))
            .Do(_ => throw new InvalidOperationException("fail"));
        var endpoint = Substitute.For<IEndpoint>();
        endpoint.CreateProducer().Returns(producer);
        var context = Substitute.For<IRouteContext>();
        context.GetEndpoint(Arg.Any<string>()).Returns(endpoint);

        var processor = new RecipientListProcessor(
            context,
            _ => new[] { "seda://a", "seda://b" },
            stopOnException: true);

        var act = () => processor.Process(new Exchange());
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public void Constructor_NullContext_Throws()
    {
        var act = () => new RecipientListProcessor(null!, _ => new[] { "a" });
        act.Should().Throw<ArgumentNullException>().WithParameterName("context");
    }

    [Fact]
    public void Constructor_NullFactory_Throws()
    {
        var act = () => new RecipientListProcessor(Substitute.For<IRouteContext>(), null!);
        act.Should().Throw<ArgumentNullException>().WithParameterName("recipientListFactory");
    }
}

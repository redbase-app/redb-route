using FluentAssertions;
using NSubstitute;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Processors;

namespace redb.Route.Tests.Processors;

/// <summary>Tests for <see cref="ToProcessor"/>.</summary>
public class ToProcessorTests
{
    /// <summary>Sends exchange to the endpoint producer.</summary>
    [Fact]
    public async Task Process_SendsToProducer()
    {
        var producer = Substitute.For<IProducer>();
        var endpoint = Substitute.For<IEndpoint>();
        endpoint.CreateProducer().Returns(producer);

        var context = Substitute.For<IRouteContext>();
        context.GetEndpoint("kafka://orders").Returns(endpoint);

        var processor = new ToProcessor("kafka://orders", context);
        var exchange = new Exchange(new Message("order-1"));

        await processor.Process(exchange);

        await producer.Received(1).Process(exchange, Arg.Any<CancellationToken>());
    }

    /// <summary>Producer is cached after first use.</summary>
    [Fact]
    public async Task Process_CachesProducer()
    {
        var producer = Substitute.For<IProducer>();
        var endpoint = Substitute.For<IEndpoint>();
        endpoint.CreateProducer().Returns(producer);

        var context = Substitute.For<IRouteContext>();
        context.GetEndpoint("kafka://orders").Returns(endpoint);

        var processor = new ToProcessor("kafka://orders", context);

        await processor.Process(new Exchange());
        await processor.Process(new Exchange());

        endpoint.Received(1).CreateProducer(); // Only once
    }

    /// <summary>EndpointUri property returns the configured URI.</summary>
    [Fact]
    public void EndpointUri_ReturnsConfigured()
    {
        var context = Substitute.For<IRouteContext>();
        var processor = new ToProcessor("redis:SET:key", context);

        processor.EndpointUri.Should().Be("redis:SET:key");
    }

    /// <summary>Null URI throws.</summary>
    [Fact]
    public void Constructor_NullUri_Throws()
    {
        var context = Substitute.For<IRouteContext>();
        var act = () => new ToProcessor(null!, context);
        act.Should().Throw<ArgumentNullException>();
    }

    /// <summary>Null context throws.</summary>
    [Fact]
    public void Constructor_NullContext_Throws()
    {
        var act = () => new ToProcessor("kafka://x", null!);
        act.Should().Throw<ArgumentNullException>();
    }

    /// <summary>RecordMessageOut is called on the endpoint after successful processing.</summary>
    [Fact]
    public async Task Process_Success_RecordsMessageOut()
    {
        var producer = Substitute.For<IProducer>();
        var endpoint = Substitute.For<IEndpoint, IEndpointStatistics>();
        endpoint.CreateProducer().Returns(producer);

        var context = Substitute.For<IRouteContext>();
        context.GetEndpoint("direct://stats").Returns(endpoint);

        var processor = new ToProcessor("direct://stats", context);
        await processor.Process(new Exchange(new Message("data")));

        ((IEndpointStatistics)endpoint).Received(1).RecordMessageOut();
        ((IEndpointStatistics)endpoint).DidNotReceive().RecordError();
    }

    /// <summary>RecordError is called on the endpoint when the producer throws.</summary>
    [Fact]
    public async Task Process_Error_RecordsError()
    {
        var producer = Substitute.For<IProducer>();
        producer.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("boom")));

        var endpoint = Substitute.For<IEndpoint, IEndpointStatistics>();
        endpoint.CreateProducer().Returns(producer);

        var context = Substitute.For<IRouteContext>();
        context.GetEndpoint("direct://stats-err").Returns(endpoint);

        var processor = new ToProcessor("direct://stats-err", context);
        var act = () => processor.Process(new Exchange(new Message("data")));
        await act.Should().ThrowAsync<InvalidOperationException>();

        ((IEndpointStatistics)endpoint).Received(1).RecordError();
        ((IEndpointStatistics)endpoint).DidNotReceive().RecordMessageOut();
    }

    /// <summary>No error when endpoint does not implement IEndpointStatistics.</summary>
    [Fact]
    public async Task Process_NonStatsEndpoint_NoError()
    {
        var producer = Substitute.For<IProducer>();
        var endpoint = Substitute.For<IEndpoint>(); // No IEndpointStatistics
        endpoint.CreateProducer().Returns(producer);

        var context = Substitute.For<IRouteContext>();
        context.GetEndpoint("mock://no-stats").Returns(endpoint);

        var processor = new ToProcessor("mock://no-stats", context);
        await processor.Process(new Exchange(new Message("data")));

        // Should not throw — graceful when endpoint doesn't implement stats
        await producer.Received(1).Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>());
    }
}

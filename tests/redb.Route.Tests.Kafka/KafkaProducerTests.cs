using System.Collections.Concurrent;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Kafka;

namespace redb.Route.Tests.Kafka;

public sealed class KafkaProducerTests
{
    private readonly KafkaComponent _component = new();

    private KafkaEndpoint CreateEndpoint(string uriStr)
    {
        var uri = EndpointUriParser.Parse(uriStr);
        return (KafkaEndpoint)_component.CreateEndpoint(uri);
    }

    [Fact]
    public void Ctor_NullEndpoint_Throws()
    {
        var opts = new KafkaEndpointOptions { Brokers = "x:9092" };
        var act = () => new KafkaProducer(null!, opts);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Ctor_NullOptions_Throws()
    {
        var ep = CreateEndpoint("kafka://t?brokers=x:9092");
        var act = () => new KafkaProducer(ep, null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task Process_BeforeStart_Throws()
    {
        var ep = CreateEndpoint("kafka://t?brokers=x:9092");
        var producer = new KafkaProducer(ep, new KafkaEndpointOptions { Brokers = "x:9092" });
        var exchange = new Exchange(new Message("test"));

        var act = () => producer.Process(exchange);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*not been started*");
    }

    [Fact]
    public void CreateProducer_ReturnsExpectedType()
    {
        var ep = CreateEndpoint("kafka://topic?brokers=localhost:9092");
        ep.CreateProducer().Should().BeOfType<KafkaProducer>();
    }
}

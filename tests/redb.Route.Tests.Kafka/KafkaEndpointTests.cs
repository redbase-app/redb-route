using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Kafka;

namespace redb.Route.Tests.Kafka;

public sealed class KafkaEndpointTests
{
    private readonly KafkaComponent _component = new();

    private KafkaEndpoint CreateEndpoint(string uriStr)
    {
        var uri = EndpointUriParser.Parse(uriStr);
        return (KafkaEndpoint)_component.CreateEndpoint(uri);
    }

    [Fact]
    public void TopicName_ExtractedFromPath()
    {
        var ep = CreateEndpoint("kafka://order-events?brokers=localhost:9092");
        ep.TopicName.Should().Be("order-events");
    }

    [Fact]
    public void CreateProducer_ReturnsKafkaProducer()
    {
        var ep = CreateEndpoint("kafka://topic1?brokers=localhost:9092");
        var producer = ep.CreateProducer();
        producer.Should().BeOfType<KafkaProducer>();
    }

    [Fact]
    public void CreateConsumer_WithGroupId_ReturnsKafkaConsumer()
    {
        var ep = CreateEndpoint("kafka://topic1?brokers=localhost:9092&groupId=test-group");
        var processor = Substitute.For<IProcessor>();
        var consumer = ep.CreateConsumer(processor);
        consumer.Should().BeOfType<KafkaConsumer>();
    }

    [Fact]
    public void CreateConsumer_WithoutGroupId_Throws()
    {
        var ep = CreateEndpoint("kafka://topic1?brokers=localhost:9092");
        var processor = Substitute.For<IProcessor>();

        var act = () => ep.CreateConsumer(processor);

        act.Should().Throw<InvalidOperationException>().WithMessage("*groupId*");
    }

    [Fact]
    public void Uri_PreservesOriginalUri()
    {
        var ep = CreateEndpoint("kafka://my-topic?brokers=b:9092");
        ep.Uri.Scheme.Should().Be("kafka");
        ep.Uri.Path.Should().Be("my-topic");
    }

    [Fact]
    public void Component_IsKafkaComponent()
    {
        var ep = CreateEndpoint("kafka://t?brokers=b:9092");
        ep.Component.Should().BeSameAs(_component);
    }
}

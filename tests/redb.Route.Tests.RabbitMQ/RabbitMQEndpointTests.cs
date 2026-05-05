using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.RabbitMQ;

namespace redb.Route.Tests.RabbitMQ;

public sealed class RabbitMQEndpointTests
{
    private readonly RabbitMQComponent _component = new();

    private RabbitMQEndpoint CreateEndpoint(string uriStr)
    {
        var uri = EndpointUriParser.Parse(uriStr);
        return (RabbitMQEndpoint)_component.CreateEndpoint(uri);
    }

    [Fact]
    public void QueueName_ExtractedFromPath()
    {
        var ep = CreateEndpoint("rabbitmq://my-queue?host=localhost");
        ep.QueueName.Should().Be("my-queue");
    }

    [Fact]
    public void CreateProducer_ReturnsRabbitMQProducer()
    {
        var ep = CreateEndpoint("rabbitmq://q?host=localhost");
        var producer = ep.CreateProducer();
        producer.Should().BeOfType<RabbitMQProducer>();
    }

    [Fact]
    public void CreateConsumer_ReturnsRabbitMQConsumer()
    {
        var ep = CreateEndpoint("rabbitmq://q?host=localhost");
        var processor = Substitute.For<IProcessor>();
        var consumer = ep.CreateConsumer(processor);
        consumer.Should().BeOfType<RabbitMQConsumer>();
    }

    [Fact]
    public void Component_IsRabbitMQComponent()
    {
        var ep = CreateEndpoint("rabbitmq://q?host=localhost");
        ep.Component.Should().BeSameAs(_component);
    }

    [Fact]
    public void Uri_PreservesOriginal()
    {
        var ep = CreateEndpoint("rabbitmq://my-queue?host=rabbit1");
        ep.Uri.Scheme.Should().Be("rabbitmq");
        ep.Uri.Path.Should().Be("my-queue");
    }
}

using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Amqp;

namespace redb.Route.Tests.Amqp;

public sealed class AmqpEndpointTests
{
    private readonly AmqpComponent _component = new();

    private AmqpEndpoint CreateEndpoint(string uriStr)
    {
        var uri = EndpointUriParser.Parse(uriStr);
        return (AmqpEndpoint)_component.CreateEndpoint(uri);
    }

    [Fact]
    public void Address_ExtractedFromPath()
    {
        var ep = CreateEndpoint("amqp://my-queue?host=localhost");
        ep.Address.Should().Be("my-queue");
    }

    [Fact]
    public void CreateProducer_ReturnsAmqpProducer()
    {
        var ep = CreateEndpoint("amqp://q?host=localhost");
        var producer = ep.CreateProducer();
        producer.Should().BeOfType<AmqpProducer>();
    }

    [Fact]
    public void CreateConsumer_ReturnsAmqpConsumer()
    {
        var ep = CreateEndpoint("amqp://q?host=localhost");
        var processor = Substitute.For<IProcessor>();
        var consumer = ep.CreateConsumer(processor);
        consumer.Should().BeOfType<AmqpConsumer>();
    }

    [Fact]
    public void Component_IsAmqpComponent()
    {
        var ep = CreateEndpoint("amqp://q?host=localhost");
        ep.Component.Should().BeSameAs(_component);
    }

    [Fact]
    public void Uri_PreservesOriginal()
    {
        var ep = CreateEndpoint("amqp://my-queue?host=broker1");
        ep.Uri.Scheme.Should().Be("amqp");
        ep.Uri.Path.Should().Be("my-queue");
    }
}

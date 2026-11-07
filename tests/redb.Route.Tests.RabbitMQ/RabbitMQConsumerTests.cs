using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.RabbitMQ;

namespace redb.Route.Tests.RabbitMQ;

public sealed class RabbitMQConsumerTests
{
    private readonly RabbitMQComponent _component = new();

    private RabbitMQEndpoint CreateEndpoint(string uriStr)
    {
        var uri = EndpointUriParser.Parse(uriStr);
        return (RabbitMQEndpoint)_component.CreateEndpoint(uri);
    }

    [Fact]
    public void Ctor_NullEndpoint_Throws()
    {
        var opts = new RabbitMQEndpointOptions();
        var proc = Substitute.For<IProcessor>();
        var act = () => new RabbitMQConsumer(null!, proc, opts);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Ctor_NullProcessor_Throws()
    {
        var ep = CreateEndpoint("rabbitmq://q?host=localhost");
        var opts = new RabbitMQEndpointOptions();
        var act = () => new RabbitMQConsumer(ep, null!, opts);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Ctor_NullOptions_Throws()
    {
        var ep = CreateEndpoint("rabbitmq://q?host=localhost");
        var proc = Substitute.For<IProcessor>();
        var act = () => new RabbitMQConsumer(ep, proc, null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void ProcessedCount_InitiallyZero()
    {
        var ep = CreateEndpoint("rabbitmq://q?host=localhost");
        var proc = Substitute.For<IProcessor>();
        var opts = new RabbitMQEndpointOptions();
        var consumer = new RabbitMQConsumer(ep, proc, opts);
        consumer.ProcessedCount.Should().Be(0);
    }
}

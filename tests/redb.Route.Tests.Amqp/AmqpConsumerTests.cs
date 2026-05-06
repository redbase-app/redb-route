using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Amqp;

namespace redb.Route.Tests.Amqp;

public sealed class AmqpConsumerTests
{
    private readonly AmqpComponent _component = new();

    private AmqpEndpoint CreateEndpoint(string uriStr)
    {
        var uri = EndpointUriParser.Parse(uriStr);
        return (AmqpEndpoint)_component.CreateEndpoint(uri);
    }

    [Fact]
    public void Ctor_NullEndpoint_Throws()
    {
        var opts = new AmqpEndpointOptions();
        var proc = Substitute.For<IProcessor>();
        var act = () => new AmqpConsumer(null!, proc, opts);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Ctor_NullProcessor_Throws()
    {
        var ep = CreateEndpoint("amqp://q?host=localhost");
        var opts = new AmqpEndpointOptions();
        var act = () => new AmqpConsumer(ep, null!, opts);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Ctor_NullOptions_Throws()
    {
        var ep = CreateEndpoint("amqp://q?host=localhost");
        var proc = Substitute.For<IProcessor>();
        var act = () => new AmqpConsumer(ep, proc, null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void ProcessedCount_InitiallyZero()
    {
        var ep = CreateEndpoint("amqp://q?host=localhost");
        var proc = Substitute.For<IProcessor>();
        var opts = new AmqpEndpointOptions();
        var consumer = new AmqpConsumer(ep, proc, opts);
        consumer.ProcessedCount.Should().Be(0);
    }
}

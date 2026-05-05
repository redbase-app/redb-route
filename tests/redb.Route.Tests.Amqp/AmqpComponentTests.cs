using redb.Route.Core;
using redb.Route.Amqp;

namespace redb.Route.Tests.Amqp;

public sealed class AmqpComponentTests
{
    private readonly AmqpComponent _sut = new();

    [Fact]
    public void Scheme_ReturnsAmqp()
    {
        _sut.Scheme.Should().Be("amqp");
    }

    [Fact]
    public void CreateEndpoint_ValidUri_ReturnsAmqpEndpoint()
    {
        var uri = EndpointUriParser.Parse("amqp://my-queue?host=localhost");

        var endpoint = _sut.CreateEndpoint(uri);

        endpoint.Should().BeOfType<AmqpEndpoint>();
        var ep = (AmqpEndpoint)endpoint;
        ep.Address.Should().Be("my-queue");
    }

    [Fact]
    public void CreateEndpoint_NullUri_Throws()
    {
        var act = () => _sut.CreateEndpoint(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void CreateEndpoint_WithConnectionParams_ParsesCorrectly()
    {
        var uri = EndpointUriParser.Parse(
            "amqp://orders?host=broker1&port=5673&user=admin&password=secret&ssl=true");

        var endpoint = (AmqpEndpoint)_sut.CreateEndpoint(uri);

        endpoint.Address.Should().Be("orders");
    }

    [Fact]
    public void CreateEndpoint_WithCapabilitiesAndRouting_ParsesCorrectly()
    {
        var uri = EndpointUriParser.Parse(
            "amqp://my-topic?host=localhost&capabilities=topic,shared&routingType=MULTICAST&durable=2");

        var endpoint = (AmqpEndpoint)_sut.CreateEndpoint(uri);
        endpoint.Address.Should().Be("my-topic");
    }

    [Fact]
    public void CreateEndpoint_WithConsumerOptions_ParsesCorrectly()
    {
        var uri = EndpointUriParser.Parse(
            "amqp://q1?host=localhost&credit=50&concurrentConsumers=4&autoAccept=false&receiveTimeout=30");

        var endpoint = (AmqpEndpoint)_sut.CreateEndpoint(uri);
        endpoint.Address.Should().Be("q1");
    }

    [Fact]
    public void CreateEndpoint_WithRpcOptions_ParsesCorrectly()
    {
        var uri = EndpointUriParser.Parse("amqp://rpc-queue?host=localhost&replyTo=true&timeout=15");
        var endpoint = (AmqpEndpoint)_sut.CreateEndpoint(uri);
        endpoint.Address.Should().Be("rpc-queue");
    }

    [Fact]
    public void CreateEndpoint_WithProducerDefaults_ParsesCorrectly()
    {
        var uri = EndpointUriParser.Parse(
            "amqp://events?host=localhost&messageDurable=false&messagePriority=7&messageTtl=60000&contentType=application/json");

        var endpoint = (AmqpEndpoint)_sut.CreateEndpoint(uri);
        endpoint.Address.Should().Be("events");
    }

    [Fact]
    public void CreateEndpoint_WithSettlementModes_ParsesCorrectly()
    {
        var uri = EndpointUriParser.Parse(
            "amqp://q?host=localhost&senderSettleMode=1&receiverSettleMode=1");

        var endpoint = (AmqpEndpoint)_sut.CreateEndpoint(uri);
        endpoint.Address.Should().Be("q");
    }

    [Fact]
    public void CreateEndpoint_WithTransactedMode_ParsesCorrectly()
    {
        var uri = EndpointUriParser.Parse("amqp://tx-queue?host=localhost&transacted=true");
        var endpoint = (AmqpEndpoint)_sut.CreateEndpoint(uri);
        endpoint.Address.Should().Be("tx-queue");
    }
}

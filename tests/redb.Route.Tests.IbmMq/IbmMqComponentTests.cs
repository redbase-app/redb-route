using redb.Route.Core;
using redb.Route.IbmMq;

namespace redb.Route.Tests.IbmMq;

public sealed class IbmMqComponentTests
{
    private readonly IbmMqComponent _sut = new();

    [Fact]
    public void Scheme_ReturnsIbmMq()
    {
        _sut.Scheme.Should().Be("wmq");
    }

    [Fact]
    public void CreateEndpoint_ValidUri_ReturnsIbmMqEndpoint()
    {
        var uri = EndpointUriParser.Parse("wmq:DEV.QUEUE.1?host=localhost&queueManager=QM1&channel=DEV.APP.SVRCONN");

        var endpoint = _sut.CreateEndpoint(uri);

        endpoint.Should().BeOfType<IbmMqEndpoint>();
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
            "wmq:ORDERS?host=broker1&port=1415&channel=APP.SVRCONN&queueManager=QM2&user=admin&password=secret");

        var endpoint = (IbmMqEndpoint)_sut.CreateEndpoint(uri);
        endpoint.Destination.Should().Be("ORDERS");
    }

    [Fact]
    public void CreateEndpoint_WithConsumerOptions_ParsesCorrectly()
    {
        var uri = EndpointUriParser.Parse(
            "wmq:Q1?host=localhost&concurrentConsumers=4&waitInterval=10000&backoutThreshold=5");

        var endpoint = (IbmMqEndpoint)_sut.CreateEndpoint(uri);
        endpoint.Destination.Should().Be("Q1");
    }

    [Fact]
    public void CreateEndpoint_WithProducerOptions_ParsesCorrectly()
    {
        var uri = EndpointUriParser.Parse(
            "wmq:OUT.Q?host=localhost&persistence=Persistent&priority=7&expiry=3000&targetClient=Mq");

        var endpoint = (IbmMqEndpoint)_sut.CreateEndpoint(uri);
        endpoint.Destination.Should().Be("OUT.Q");
    }

    [Fact]
    public void CreateEndpoint_WithTopicDestination_ParsesCorrectly()
    {
        var uri = EndpointUriParser.Parse(
            "wmq:EVENTS/ORDER?host=localhost&destinationType=Topic");

        var endpoint = (IbmMqEndpoint)_sut.CreateEndpoint(uri);
        endpoint.Destination.Should().Be("EVENTS/ORDER");
    }

    [Fact]
    public void CreateEndpoint_WithTransactedMode_ParsesCorrectly()
    {
        var uri = EndpointUriParser.Parse("wmq:TX.Q?host=localhost&transacted=true");
        var endpoint = (IbmMqEndpoint)_sut.CreateEndpoint(uri);
        endpoint.Destination.Should().Be("TX.Q");
    }

    [Fact]
    public void CreateEndpoint_WithRpcOptions_ParsesCorrectly()
    {
        var uri = EndpointUriParser.Parse(
            "wmq:RPC.Q?host=localhost&replyTo=true&timeout=60&correlationPattern=CorrelId");

        var endpoint = (IbmMqEndpoint)_sut.CreateEndpoint(uri);
        endpoint.Destination.Should().Be("RPC.Q");
    }

    [Fact]
    public void CreateEndpoint_WithSslOptions_ParsesCorrectly()
    {
        var uri = EndpointUriParser.Parse(
            "wmq:SECURE.Q?host=localhost&sslCipherSpec=TLS_RSA_WITH_AES_256_CBC_SHA256&sslPeerName=CN%3Dbroker1");

        var endpoint = (IbmMqEndpoint)_sut.CreateEndpoint(uri);
        endpoint.Destination.Should().Be("SECURE.Q");
    }
}

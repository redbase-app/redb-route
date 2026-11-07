using redb.Route.Abstractions;
using redb.Route.Tcp;

namespace redb.Route.Tests.Tcp;

public class TcpComponentTests
{
    [Fact]
    public void Scheme_ReturnsTcp()
    {
        var component = new TcpComponent();
        component.Scheme.Should().Be("tcp");
    }

    [Fact]
    public void CreateEndpoint_ValidUri_ReturnsTcpEndpoint()
    {
        var component = new TcpComponent();
        var uri = new EndpointUri("tcp", "/localhost:9090", "tcp:localhost:9090", new Dictionary<string, string>());

        var endpoint = component.CreateEndpoint(uri);

        endpoint.Should().BeOfType<TcpEndpoint>();
        endpoint.Uri.Should().BeSameAs(uri);
        endpoint.Component.Should().BeSameAs(component);
    }

    [Fact]
    public void CreateEndpoint_NullUri_Throws()
    {
        var component = new TcpComponent();
        var act = () => component.CreateEndpoint(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void CreateEndpoint_ParsesHostPort()
    {
        var component = new TcpComponent();
        var uri = new EndpointUri("tcp", "/myhost:5555", "tcp:myhost:5555", new Dictionary<string, string>());

        var endpoint = (TcpEndpoint)component.CreateEndpoint(uri);

        endpoint.EndpointOptions.Host.Should().Be("myhost");
        endpoint.EndpointOptions.Port.Should().Be(5555);
    }

    [Fact]
    public void CreateEndpoint_WithOptions_BindsCorrectly()
    {
        var component = new TcpComponent();
        var parameters = new Dictionary<string, string>
        {
            ["textLine"] = "true",
            ["delimiter"] = "|",
            ["keepAlive"] = "false",
            ["noDelay"] = "false",
            ["receiveBufferSize"] = "16384",
            ["sendBufferSize"] = "16384",
            ["connectTimeout"] = "5000"
        };
        var uri = new EndpointUri("tcp", "/localhost:9090", "tcp:localhost:9090", parameters);

        var endpoint = (TcpEndpoint)component.CreateEndpoint(uri);

        endpoint.EndpointOptions.Framing.Should().Be(TcpFraming.TextLine);
        endpoint.EndpointOptions.Delimiter.Should().Be("|");
        endpoint.EndpointOptions.KeepAlive.Should().BeFalse();
        endpoint.EndpointOptions.NoDelay.Should().BeFalse();
        endpoint.EndpointOptions.ReceiveBufferSize.Should().Be(16384);
        endpoint.EndpointOptions.SendBufferSize.Should().Be(16384);
        endpoint.EndpointOptions.ConnectTimeout.Should().Be(5000);
    }

    [Fact]
    public void CreateEndpoint_ConsumerOptions_BindsCorrectly()
    {
        var component = new TcpComponent();
        var parameters = new Dictionary<string, string>
        {
            ["backlog"] = "256",
            ["maxConnections"] = "100",
            ["inOut"] = "true"
        };
        var uri = new EndpointUri("tcp", "/0.0.0.0:8080", "tcp:0.0.0.0:8080", parameters);

        var endpoint = (TcpEndpoint)component.CreateEndpoint(uri);

        endpoint.EndpointOptions.Backlog.Should().Be(256);
        endpoint.EndpointOptions.MaxConnections.Should().Be(100);
        endpoint.EndpointOptions.InOut.Should().BeTrue();
    }

    [Fact]
    public void CreateEndpoint_LengthPrefixedFraming()
    {
        var component = new TcpComponent();
        var parameters = new Dictionary<string, string>
        {
            ["lengthPrefixed"] = "true"
        };
        var uri = new EndpointUri("tcp", "/localhost:9090", "tcp:localhost:9090", parameters);

        var endpoint = (TcpEndpoint)component.CreateEndpoint(uri);

        endpoint.EndpointOptions.Framing.Should().Be(TcpFraming.LengthPrefixed);
    }

    [Fact]
    public void CreateEndpoint_FramingEnum_Direct()
    {
        var component = new TcpComponent();
        var parameters = new Dictionary<string, string>
        {
            ["framing"] = "TextLine"
        };
        var uri = new EndpointUri("tcp", "/localhost:9090", "tcp:localhost:9090", parameters);

        var endpoint = (TcpEndpoint)component.CreateEndpoint(uri);

        endpoint.EndpointOptions.Framing.Should().Be(TcpFraming.TextLine);
    }

    [Fact]
    public void CreateEndpoint_ReconnectOptions()
    {
        var component = new TcpComponent();
        var parameters = new Dictionary<string, string>
        {
            ["reconnect"] = "true",
            ["reconnectInterval"] = "2000",
            ["maxReconnectAttempts"] = "5"
        };
        var uri = new EndpointUri("tcp", "/localhost:9090", "tcp:localhost:9090", parameters);

        var endpoint = (TcpEndpoint)component.CreateEndpoint(uri);

        endpoint.EndpointOptions.Reconnect.Should().BeTrue();
        endpoint.EndpointOptions.ReconnectInterval.Should().Be(2000);
        endpoint.EndpointOptions.MaxReconnectAttempts.Should().Be(5);
    }

    [Fact]
    public void CreateProducer_ReturnsTcpProducer()
    {
        var component = new TcpComponent();
        var uri = new EndpointUri("tcp", "/localhost:9090", "tcp:localhost:9090", new Dictionary<string, string>());
        var endpoint = component.CreateEndpoint(uri);

        var producer = endpoint.CreateProducer();

        producer.Should().BeOfType<TcpProducer>();
    }

    [Fact]
    public void CreateConsumer_ReturnsTcpConsumer()
    {
        var component = new TcpComponent();
        var uri = new EndpointUri("tcp", "/0.0.0.0:9090", "tcp:0.0.0.0:9090", new Dictionary<string, string>());
        var endpoint = component.CreateEndpoint(uri);
        var processor = Substitute.For<IProcessor>();

        var consumer = endpoint.CreateConsumer(processor);

        consumer.Should().BeOfType<TcpConsumer>();
    }

    [Fact]
    public void CreateConsumer_NullProcessor_Throws()
    {
        var component = new TcpComponent();
        var uri = new EndpointUri("tcp", "/0.0.0.0:9090", "tcp:0.0.0.0:9090", new Dictionary<string, string>());
        var endpoint = component.CreateEndpoint(uri);

        var act = () => endpoint.CreateConsumer(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Theory]
    [InlineData("/localhost:9090", "localhost", 9090)]
    [InlineData("/0.0.0.0:8080", "0.0.0.0", 8080)]
    [InlineData("127.0.0.1:5555", "127.0.0.1", 5555)]
    [InlineData("/myserver:443", "myserver", 443)]
    public void ParseHostPort_VariousFormats(string path, string expectedHost, int expectedPort)
    {
        var options = new TcpEndpointOptions();
        TcpComponent.ParseHostPort(path, options);

        options.Host.Should().Be(expectedHost);
        options.Port.Should().Be(expectedPort);
    }
}

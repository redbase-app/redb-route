using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.WebSocket;

namespace redb.Route.Tests.WebSocket;

public class WsComponentTests
{
    [Fact]
    public void Scheme_IsWs()
    {
        var component = new WsComponent();
        component.Scheme.Should().Be("ws");
    }

    [Fact]
    public void WssComponent_Scheme_IsWss()
    {
        var component = new WssComponent();
        component.Scheme.Should().Be("wss");
    }

    [Fact]
    public void CreateEndpoint_ValidUri_ReturnsWsEndpoint()
    {
        var component = new WsComponent();
        var uri = new EndpointUri("ws", "/127.0.0.1:9000/chat", "ws:127.0.0.1:9000/chat",
            new Dictionary<string, string>());
        var endpoint = component.CreateEndpoint(uri);
        endpoint.Should().BeOfType<WsEndpoint>();
    }

    [Fact]
    public void CreateEndpoint_NullUri_Throws()
    {
        var component = new WsComponent();
        var act = () => component.CreateEndpoint(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Theory]
    [InlineData("/127.0.0.1:9000/chat", "127.0.0.1", 9000)]
    [InlineData("localhost:8080", "localhost", 8080)]
    [InlineData("/0.0.0.0:5555/feed", "0.0.0.0", 5555)]
    [InlineData("/myhost:443", "myhost", 443)]
    public void ParseHostPort_ExtractsCorrectly(string path, string expectedHost, int expectedPort)
    {
        var options = new WsEndpointOptions();
        WsComponent.ParseHostPort(path, options);
        options.Host.Should().Be(expectedHost);
        options.Port.Should().Be(expectedPort);
    }

    [Theory]
    [InlineData("/127.0.0.1:9000/chat", "/chat")]
    [InlineData("/0.0.0.0:5000", "/")]
    [InlineData("/host:8080/a/b/c", "/a/b/c")]
    public void ExtractPath_CorrectPath(string uriPath, string expected)
    {
        WsComponent.ExtractPath(uriPath).Should().Be(expected);
    }

    [Fact]
    public void CreateEndpoint_BindsMessageType()
    {
        var component = new WsComponent();
        var uri = new EndpointUri("ws", "/127.0.0.1:9000", "ws:127.0.0.1:9000",
            new Dictionary<string, string> { ["messageType"] = "Binary" });
        var endpoint = (WsEndpoint)component.CreateEndpoint(uri);
        endpoint.EndpointOptions.MessageType.Should().Be(WsMessageType.Binary);
    }

    [Fact]
    public void CreateEndpoint_BindsSubProtocol()
    {
        var component = new WsComponent();
        var uri = new EndpointUri("ws", "/127.0.0.1:9000", "ws:127.0.0.1:9000",
            new Dictionary<string, string> { ["subProtocol"] = "graphql-ws" });
        var endpoint = (WsEndpoint)component.CreateEndpoint(uri);
        endpoint.EndpointOptions.SubProtocol.Should().Be("graphql-ws");
    }

    [Fact]
    public void CreateEndpoint_BindsKeepAliveInterval()
    {
        var component = new WsComponent();
        var uri = new EndpointUri("ws", "/127.0.0.1:9000", "ws:127.0.0.1:9000",
            new Dictionary<string, string> { ["keepAliveInterval"] = "5000" });
        var endpoint = (WsEndpoint)component.CreateEndpoint(uri);
        endpoint.EndpointOptions.KeepAliveInterval.Should().Be(5000);
    }

    [Fact]
    public void CreateEndpoint_BindsReconnectOptions()
    {
        var component = new WsComponent();
        var uri = new EndpointUri("ws", "/127.0.0.1:9000", "ws:127.0.0.1:9000",
            new Dictionary<string, string>
            {
                ["reconnect"] = "true",
                ["reconnectInterval"] = "3000",
                ["maxReconnectAttempts"] = "5"
            });
        var endpoint = (WsEndpoint)component.CreateEndpoint(uri);
        endpoint.EndpointOptions.Reconnect.Should().BeTrue();
        endpoint.EndpointOptions.ReconnectInterval.Should().Be(3000);
        endpoint.EndpointOptions.MaxReconnectAttempts.Should().Be(5);
    }

    [Fact]
    public void CreateEndpoint_BindsConsumerOptions()
    {
        var component = new WsComponent();
        var uri = new EndpointUri("ws", "/127.0.0.1:9000", "ws:127.0.0.1:9000",
            new Dictionary<string, string>
            {
                ["maxConnections"] = "50",
                ["inOut"] = "true"
            });
        var endpoint = (WsEndpoint)component.CreateEndpoint(uri);
        endpoint.EndpointOptions.MaxConnections.Should().Be(50);
        endpoint.EndpointOptions.InOut.Should().BeTrue();
    }

    [Fact]
    public void WssComponent_CreateEndpoint_SetsSslTrue()
    {
        var component = new WssComponent();
        var uri = new EndpointUri("wss", "/127.0.0.1:443", "wss:127.0.0.1:443",
            new Dictionary<string, string>());
        var endpoint = (WsEndpoint)component.CreateEndpoint(uri);
        endpoint.EndpointOptions.Ssl.Should().BeTrue();
    }

    [Fact]
    public void ConsumerPath_Extracted()
    {
        var component = new WsComponent();
        var uri = new EndpointUri("ws", "/127.0.0.1:9000/chat", "ws:127.0.0.1:9000/chat",
            new Dictionary<string, string>());
        var endpoint = (WsEndpoint)component.CreateEndpoint(uri);
        endpoint.ConsumerPath.Should().Be("/chat");
    }

    [Fact]
    public void BuildProducerUrl_CorrectFormat()
    {
        var component = new WsComponent();
        var uri = new EndpointUri("ws", "/echo.example.com:8080/feed", "ws:echo.example.com:8080/feed",
            new Dictionary<string, string>());
        var endpoint = (WsEndpoint)component.CreateEndpoint(uri);
        endpoint.BuildProducerUrl().Should().Be("ws://echo.example.com:8080/feed");
    }

    [Fact]
    public void BuildProducerUrl_Wss_CorrectScheme()
    {
        var component = new WssComponent();
        var uri = new EndpointUri("wss", "/secure.example.com:443/ws", "wss:secure.example.com:443/ws",
            new Dictionary<string, string>());
        var endpoint = (WsEndpoint)component.CreateEndpoint(uri);
        endpoint.BuildProducerUrl().Should().Be("wss://secure.example.com:443/ws");
    }

    [Fact]
    public void CreateProducer_ReturnsWsProducer()
    {
        var component = new WsComponent();
        var uri = new EndpointUri("ws", "/127.0.0.1:9000", "ws:127.0.0.1:9000",
            new Dictionary<string, string>());
        var endpoint = (WsEndpoint)component.CreateEndpoint(uri);
        endpoint.CreateProducer().Should().BeOfType<WsProducer>();
    }

    [Fact]
    public void CreateConsumer_ReturnsWsConsumer()
    {
        var component = new WsComponent();
        var uri = new EndpointUri("ws", "/127.0.0.1:9000", "ws:127.0.0.1:9000",
            new Dictionary<string, string>());
        var endpoint = (WsEndpoint)component.CreateEndpoint(uri);
        var processor = Substitute.For<redb.Route.Abstractions.IProcessor>();
        endpoint.CreateConsumer(processor).Should().BeOfType<WsConsumer>();
    }

    [Fact]
    public void CreateConsumer_NullProcessor_Throws()
    {
        var component = new WsComponent();
        var uri = new EndpointUri("ws", "/127.0.0.1:9000", "ws:127.0.0.1:9000",
            new Dictionary<string, string>());
        var endpoint = (WsEndpoint)component.CreateEndpoint(uri);
        var act = () => endpoint.CreateConsumer(null!);
        act.Should().Throw<ArgumentNullException>();
    }
}

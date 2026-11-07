using redb.Route.Core;
using redb.Route.WebSocket;

namespace redb.Route.Tests.WebSocket;

public class WsBuilderTests
{
    // ── Factory ─────────────────────────────────────────────────────

    [Fact]
    public void Listen_StartsWithWsScheme()
    {
        var uri = Ws.Listen("0.0.0.0:8080/chat").Build();
        uri.Should().StartWith("ws:0.0.0.0:8080/chat");
    }

    [Fact]
    public void Connect_StartsWithWsScheme()
    {
        var uri = Ws.Connect("api.example.com:443/stream").Build();
        uri.Should().StartWith("ws:api.example.com:443/stream");
    }

    [Fact]
    public void NullHostPort_Throws()
    {
        var act = () => Ws.Listen(null!);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void EmptyHostPort_Throws()
    {
        var act = () => Ws.Connect("");
        act.Should().Throw<ArgumentException>();
    }

    // ── Framing ─────────────────────────────────────────────────────

    [Fact]
    public void Binary_SetsParam()
    {
        var uri = Ws.Connect("h:8080/ws").Binary().Build();
        uri.Should().Contain("messageType=Binary");
    }

    [Fact]
    public void Encoding_SetsParam()
    {
        var uri = Ws.Connect("h:8080/ws").Encoding("utf-16").Build();
        uri.Should().Contain("encoding=utf-16");
    }

    [Fact]
    public void SubProtocol_SetsParam()
    {
        var uri = Ws.Listen("0.0.0.0:8080/ws").SubProtocol("graphql-ws").Build();
        uri.Should().Contain("subProtocol=graphql-ws");
    }

    // ── Socket ──────────────────────────────────────────────────────

    [Fact]
    public void ReceiveBufferSize_SetsParam()
    {
        var uri = Ws.Listen("h:8080/ws").ReceiveBufferSize(65536).Build();
        uri.Should().Contain("receiveBufferSize=65536");
    }

    [Fact]
    public void SendBufferSize_SetsParam()
    {
        var uri = Ws.Connect("h:8080/ws").SendBufferSize(32768).Build();
        uri.Should().Contain("sendBufferSize=32768");
    }

    [Fact]
    public void KeepAliveInterval_SetsParam()
    {
        var uri = Ws.Connect("h:8080/ws").KeepAliveInterval(15000).Build();
        uri.Should().Contain("keepAliveInterval=15000");
    }

    // ── Producer ────────────────────────────────────────────────────

    [Fact]
    public void ConnectTimeout_SetsParam()
    {
        var uri = Ws.Connect("h:8080/ws").ConnectTimeout(5000).Build();
        uri.Should().Contain("connectTimeout=5000");
    }

    [Fact]
    public void Reconnect_SetsParams()
    {
        var uri = Ws.Connect("h:8080/ws").Reconnect(3000, 10).Build();
        uri.Should().Contain("reconnect=true");
        uri.Should().Contain("reconnectInterval=3000");
        uri.Should().Contain("maxReconnectAttempts=10");
    }

    // ── Consumer ────────────────────────────────────────────────────

    [Fact]
    public void MaxConnections_SetsParam()
    {
        var uri = Ws.Listen("0.0.0.0:8080/ws").MaxConnections(100).Build();
        uri.Should().Contain("maxConnections=100");
    }

    [Fact]
    public void InOut_SetsParam()
    {
        var uri = Ws.Listen("0.0.0.0:8080/ws").InOut().Build();
        uri.Should().Contain("inOut=true");
    }

    // ── TLS ─────────────────────────────────────────────────────────

    [Fact]
    public void Ssl_ChangesSchemeToWss()
    {
        var uri = Ws.Connect("api.example.com/stream").Ssl().Build();
        uri.Should().StartWith("wss:api.example.com/stream");
    }

    [Fact]
    public void Ssl_SetsParamTrue()
    {
        var uri = Ws.Connect("h:443/ws").Ssl().Build();
        uri.Should().Contain("ssl=true");
    }

    [Fact]
    public void SslCertPath_SetsParam()
    {
        var uri = Ws.Listen("0.0.0.0:443/ws").SslCertPath("/certs/cert.pfx").Build();
        uri.Should().Contain("sslCertPath=");
    }

    // ── Conversion ──────────────────────────────────────────────────

    [Fact]
    public void ImplicitConversion_ReturnsUri()
    {
        string uri = Ws.Listen("0.0.0.0:8080/chat").SubProtocol("json").InOut();
        uri.Should().StartWith("ws:0.0.0.0:8080/chat?");
    }

    [Fact]
    public void ToString_ReturnsSameAsBuild()
    {
        var builder = Ws.Connect("h:8080/ws").Binary().Reconnect();
        builder.ToString().Should().Be(builder.Build());
    }

    // ── Full chain ──────────────────────────────────────────────────

    [Fact]
    public void FullChain_Consumer_BuildsCorrectUri()
    {
        var uri = Ws.Listen("0.0.0.0:8080/chat")
            .SubProtocol("json")
            .ReceiveBufferSize(65536)
            .MaxConnections(200)
            .InOut()
            .Build();

        uri.Should().StartWith("ws:0.0.0.0:8080/chat?");
        uri.Should().Contain("subProtocol=json");
        uri.Should().Contain("inOut=true");
    }

    [Fact]
    public void FullChain_Producer_BuildsCorrectUri()
    {
        var uri = Ws.Connect("api.example.com:443/stream")
            .Ssl()
            .Binary()
            .ConnectTimeout(5000)
            .Reconnect(3000, 5)
            .Build();

        uri.Should().StartWith("wss:api.example.com:443/stream?");
        uri.Should().Contain("messageType=Binary");
        uri.Should().Contain("reconnect=true");
    }

    // ── Round-trip ──────────────────────────────────────────────────

    [Fact]
    public void RoundTrip_ParseAndReconstruct()
    {
        var original = Ws.Listen("0.0.0.0:8080/chat").SubProtocol("json").Build();
        var parsed = EndpointUriParser.Parse(original);
        parsed.Scheme.Should().Be("ws");
        parsed.RawParameters["subProtocol"].Should().Be("json");
    }

    [Fact]
    public void RoundTrip_Wss_ParseAndReconstruct()
    {
        var original = Ws.Connect("h:443/stream").Ssl().Build();
        var parsed = EndpointUriParser.Parse(original);
        parsed.Scheme.Should().Be("wss");
    }
}

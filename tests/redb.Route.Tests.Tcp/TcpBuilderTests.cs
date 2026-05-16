using redb.Route.Core;
using redb.Route.Tcp;

namespace redb.Route.Tests.Tcp;

public class TcpBuilderTests
{
    // ── Factory ─────────────────────────────────────────────────────

    [Fact]
    public void Listen_StartsWithTcpScheme()
    {
        var uri = TcpDsl.Listen("0.0.0.0:9000").Build();
        uri.Should().StartWith("tcp:0.0.0.0:9000");
    }

    [Fact]
    public void Connect_StartsWithTcpScheme()
    {
        var uri = TcpDsl.Connect("192.168.1.10:9000").Build();
        uri.Should().StartWith("tcp:192.168.1.10:9000");
    }

    [Fact]
    public void NullHostPort_Throws()
    {
        var act = () => TcpDsl.Listen(null!);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void EmptyHostPort_Throws()
    {
        var act = () => TcpDsl.Connect("");
        act.Should().Throw<ArgumentException>();
    }

    // ── Framing ─────────────────────────────────────────────────────

    [Fact]
    public void TextLine_SetsParam()
    {
        var uri = TcpDsl.Listen("0.0.0.0:9000").TextLine().Build();
        uri.Should().Contain("textLine=true");
    }

    [Fact]
    public void LengthPrefixed_SetsParam()
    {
        var uri = TcpDsl.Connect("h:9000").LengthPrefixed().Build();
        uri.Should().Contain("lengthPrefixed=true");
    }

    [Fact]
    public void Delimiter_SetsParam()
    {
        var uri = TcpDsl.Listen("h:9000").Delimiter("|").Build();
        uri.Should().Contain("delimiter=");
    }

    [Fact]
    public void Encoding_SetsParam()
    {
        var uri = TcpDsl.Listen("h:9000").Encoding("utf-16").Build();
        uri.Should().Contain("encoding=utf-16");
    }

    // ── Socket ──────────────────────────────────────────────────────

    [Fact]
    public void KeepAlive_SetsParam()
    {
        var uri = TcpDsl.Connect("h:9000").KeepAlive().Build();
        uri.Should().Contain("keepAlive=true");
    }

    [Fact]
    public void NoDelay_SetsParam()
    {
        var uri = TcpDsl.Connect("h:9000").NoDelay().Build();
        uri.Should().Contain("noDelay=true");
    }

    [Fact]
    public void ReceiveBufferSize_SetsParam()
    {
        var uri = TcpDsl.Listen("h:9000").ReceiveBufferSize(65536).Build();
        uri.Should().Contain("receiveBufferSize=65536");
    }

    [Fact]
    public void SendBufferSize_SetsParam()
    {
        var uri = TcpDsl.Connect("h:9000").SendBufferSize(32768).Build();
        uri.Should().Contain("sendBufferSize=32768");
    }

    // ── Producer ────────────────────────────────────────────────────

    [Fact]
    public void ConnectTimeout_SetsParam()
    {
        var uri = TcpDsl.Connect("h:9000").ConnectTimeout(5000).Build();
        uri.Should().Contain("connectTimeout=5000");
    }

    [Fact]
    public void Reconnect_SetsParams()
    {
        var uri = TcpDsl.Connect("h:9000").Reconnect(3000, 10).Build();
        uri.Should().Contain("reconnect=true");
        uri.Should().Contain("reconnectInterval=3000");
        uri.Should().Contain("maxReconnectAttempts=10");
    }

    // ── Consumer ────────────────────────────────────────────────────

    [Fact]
    public void Backlog_SetsParam()
    {
        var uri = TcpDsl.Listen("0.0.0.0:9000").Backlog(256).Build();
        uri.Should().Contain("backlog=256");
    }

    [Fact]
    public void MaxConnections_SetsParam()
    {
        var uri = TcpDsl.Listen("0.0.0.0:9000").MaxConnections(100).Build();
        uri.Should().Contain("maxConnections=100");
    }

    [Fact]
    public void InOut_SetsParam()
    {
        var uri = TcpDsl.Listen("0.0.0.0:9000").InOut().Build();
        uri.Should().Contain("inOut=true");
    }

    // ── TLS ─────────────────────────────────────────────────────────

    [Fact]
    public void Ssl_SetsParam()
    {
        var uri = TcpDsl.Listen("0.0.0.0:9000").Ssl().Build();
        uri.Should().Contain("ssl=true");
    }

    [Fact]
    public void SslCertPath_SetsParam()
    {
        var uri = TcpDsl.Listen("0.0.0.0:9000").SslCertPath("/certs/cert.pfx").Build();
        uri.Should().Contain("sslCertPath=");
    }

    [Fact]
    public void SslTargetHost_SetsParam()
    {
        var uri = TcpDsl.Connect("h:9000").SslTargetHost("api.example.com").Build();
        uri.Should().Contain("sslTargetHost=api.example.com");
    }

    // ── Conversion ──────────────────────────────────────────────────

    [Fact]
    public void ImplicitConversion_ReturnsUri()
    {
        string uri = TcpDsl.Listen("0.0.0.0:9000").TextLine().InOut();
        uri.Should().StartWith("tcp:0.0.0.0:9000?");
    }

    [Fact]
    public void ToString_ReturnsSameAsBuild()
    {
        var builder = TcpDsl.Connect("h:9000").TextLine().Reconnect();
        builder.ToString().Should().Be(builder.Build());
    }

    // ── Full chain ──────────────────────────────────────────────────

    [Fact]
    public void FullChain_Consumer_BuildsCorrectUri()
    {
        var uri = TcpDsl.Listen("0.0.0.0:9000")
            .TextLine()
            .KeepAlive()
            .NoDelay()
            .Backlog(256)
            .MaxConnections(100)
            .InOut()
            .Build();

        uri.Should().StartWith("tcp:0.0.0.0:9000?");
        uri.Should().Contain("textLine=true");
        uri.Should().Contain("inOut=true");
    }

    [Fact]
    public void FullChain_Producer_BuildsCorrectUri()
    {
        var uri = TcpDsl.Connect("192.168.1.10:9000")
            .TextLine()
            .ConnectTimeout(5000)
            .Reconnect(3000, 5)
            .Ssl()
            .SslTargetHost("api.example.com")
            .Build();

        uri.Should().StartWith("tcp:192.168.1.10:9000?");
        uri.Should().Contain("reconnect=true");
        uri.Should().Contain("ssl=true");
    }

    // ── Round-trip ──────────────────────────────────────────────────

    [Fact]
    public void RoundTrip_ParseAndReconstruct()
    {
        var original = TcpDsl.Listen("0.0.0.0:9000").TextLine().InOut().Build();
        var parsed = EndpointUriParser.Parse(original);
        parsed.Scheme.Should().Be("tcp");
        parsed.RawParameters["textLine"].Should().Be("true");
        parsed.RawParameters["inOut"].Should().Be("true");
    }
}

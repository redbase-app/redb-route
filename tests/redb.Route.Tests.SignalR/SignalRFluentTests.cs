using redb.Route.SignalR;
using SignalRDsl = redb.Route.SignalR.SignalR;

namespace redb.Route.Tests.SignalR;

/// <summary>
/// Unit tests for SignalR fluent DSL builder.
/// </summary>
public class SignalRFluentTests
{
    // ── Hub (consumer) ──

    [Fact]
    public void Hub_BasicUri()
    {
        string uri = SignalRDsl.Hub("0.0.0.0:5000/chatHub");
        uri.Should().Be("signalr:0.0.0.0:5000/chatHub");
    }

    [Fact]
    public void Hub_WithOptions()
    {
        string uri = SignalRDsl.Hub("0.0.0.0:5000/chatHub")
            .Method("Send")
            .InOut()
            .DefaultGroup("lobby");

        uri.Should().Contain("method=Send");
        uri.Should().Contain("inOut=true");
        uri.Should().Contain("defaultGroup=lobby");
    }

    // ── Connect (producer client) ──

    [Fact]
    public void Connect_IncludesClientMode()
    {
        string uri = SignalRDsl.Connect("api.example.com:5000/chatHub");
        uri.Should().Contain("mode=client");
    }

    [Fact]
    public void Connect_WithReconnect()
    {
        string uri = SignalRDsl.Connect("api.example.com:5000/chatHub")
            .Method("Send")
            .Reconnect(3000, 10);

        uri.Should().Contain("mode=client");
        uri.Should().Contain("method=Send");
        uri.Should().Contain("reconnect=true");
        uri.Should().Contain("reconnectInterval=3000");
        uri.Should().Contain("maxReconnectAttempts=10");
    }

    [Fact]
    public void Connect_WithSsl()
    {
        string uri = SignalRDsl.Connect("api.example.com:5000/chatHub").Ssl();
        uri.Should().Contain("ssl=true");
    }

    [Fact]
    public void Connect_WithAccessToken()
    {
        string uri = SignalRDsl.Connect("api.example.com:5000/chatHub")
            .AccessToken("my-jwt-token");

        uri.Should().Contain("accessToken=my-jwt-token");
    }

    [Fact]
    public void Connect_Direct_AddsBridgeFalse()
    {
        string uri = SignalRDsl.Connect("api.example.com:5000/chatHub")
            .Method("BroadcastMessage")
            .Direct();

        uri.Should().Contain("bridge=false");
        uri.Should().Contain("method=BroadcastMessage");
    }

    [Fact]
    public void Connect_Bridge_DoesNotAppendBridgeParam()
    {
        string uri = SignalRDsl.Connect("api.example.com:5000/chatHub");
        uri.Should().NotContain("bridge");
    }

    [Fact]
    public void Connect_WithTransport()
    {
        string uri = SignalRDsl.Connect("api.example.com:5000/hub")
            .Transport(SignalRTransport.LongPolling);

        uri.Should().Contain("transport=LongPolling");
    }

    // ── Broadcast (producer server) ──

    [Fact]
    public void Broadcast_IncludesServerMode()
    {
        string uri = SignalRDsl.Broadcast("0.0.0.0:5000/chatHub");
        uri.Should().Contain("mode=server");
    }

    [Fact]
    public void Broadcast_WithGroupTarget()
    {
        string uri = SignalRDsl.Broadcast("0.0.0.0:5000/chatHub")
            .Method("Notify")
            .Group("room1");

        uri.Should().Contain("mode=server");
        uri.Should().Contain("method=Notify");
        uri.Should().Contain("targetType=group");
        uri.Should().Contain("targetGroup=room1");
    }

    [Fact]
    public void Broadcast_WithMessagePack()
    {
        string uri = SignalRDsl.Broadcast("0.0.0.0:5000/chatHub").MessagePack();
        uri.Should().Contain("messagePack=true");
    }

    // ── Implicit conversion ──

    [Fact]
    public void ImplicitConversion_ToString()
    {
        string uri = SignalRDsl.Hub("0.0.0.0:5000/chatHub");
        uri.Should().StartWith("signalr:");
    }

    [Fact]
    public void ToString_SameAsBuild()
    {
        var builder = SignalRDsl.Hub("0.0.0.0:5000/chatHub").InOut().Method("Send");
        builder.ToString().Should().Be(builder.Build());
    }

    // ── Combined scenario ──

    [Fact]
    public void FullBuilder_AllOptions()
    {
        string uri = SignalRDsl.Connect("api.example.com:5000/chatHub")
            .Method("SendMessage")
            .InOut()
            .Transport(SignalRTransport.ServerSentEvents)
            .MessagePack()
            .Ssl()
            .SslCertPath("/certs/cert.pfx")
            .SslCertPassword("p@ss")
            .Reconnect(2000, 3)
            .AccessToken("token123");

        uri.Should().Contain("signalr:api.example.com:5000/chatHub");
        uri.Should().Contain("mode=client");
        uri.Should().Contain("method=SendMessage");
        uri.Should().Contain("inOut=true");
        uri.Should().Contain("transport=ServerSentEvents");
        uri.Should().Contain("messagePack=true");
        uri.Should().Contain("ssl=true");
        uri.Should().Contain("reconnect=true");
        uri.Should().Contain("reconnectInterval=2000");
        uri.Should().Contain("maxReconnectAttempts=3");
        uri.Should().Contain("accessToken=token123");
    }
}

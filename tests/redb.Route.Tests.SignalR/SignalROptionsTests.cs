using redb.Route.SignalR;

namespace redb.Route.Tests.SignalR;

/// <summary>
/// Unit tests for SignalREndpointOptions: defaults, validation, binding.
/// </summary>
public class SignalROptionsTests
{
    // ── Defaults ──

    [Fact]
    public void Defaults_AreCorrect()
    {
        var opts = new SignalREndpointOptions();

        opts.Host.Should().Be("0.0.0.0");
        opts.Port.Should().Be(5000);
        opts.Mode.Should().Be(SignalRMode.Client);
        opts.Method.Should().BeNull();
        opts.DefaultGroup.Should().BeNull();
        opts.InOut.Should().BeFalse();
        opts.Bridge.Should().BeTrue();
        opts.Transport.Should().Be(SignalRTransport.WebSockets);
        opts.MessagePack.Should().BeFalse();
        opts.Reconnect.Should().BeFalse();
        opts.ReconnectInterval.Should().Be(5000);
        opts.MaxReconnectAttempts.Should().Be(0);
        opts.TargetType.Should().Be("All");
        opts.TargetGroup.Should().BeNull();
        opts.Ssl.Should().BeFalse();
        opts.SslCertPath.Should().BeNull();
        opts.SslCertPassword.Should().BeNull();
        opts.AccessToken.Should().BeNull();
    }

    // ── Validation ──

    [Fact]
    public void Validate_ValidOptions_DoesNotThrow()
    {
        var opts = new SignalREndpointOptions();
        opts.Invoking(o => o.Validate()).Should().NotThrow();
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(70000)]
    public void Validate_InvalidPort_Throws(int port)
    {
        var opts = new SignalREndpointOptions { Port = port };
        opts.Invoking(o => o.Validate()).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Validate_ZeroReconnectInterval_Throws()
    {
        var opts = new SignalREndpointOptions { ReconnectInterval = 0 };
        opts.Invoking(o => o.Validate()).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Validate_NegativeReconnectInterval_Throws()
    {
        var opts = new SignalREndpointOptions { ReconnectInterval = -1 };
        opts.Invoking(o => o.Validate()).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Validate_NegativeMaxReconnectAttempts_Throws()
    {
        var opts = new SignalREndpointOptions { MaxReconnectAttempts = -1 };
        opts.Invoking(o => o.Validate()).Should().Throw<ArgumentException>();
    }

    // ── BindFromUri ──

    [Fact]
    public void BindFromUri_SetsAllOptions()
    {
        var opts = new SignalREndpointOptions();
        var parameters = new Dictionary<string, string>
        {
            ["mode"] = "Server",
            ["method"] = "SendMessage",
            ["defaultGroup"] = "lobby",
            ["inOut"] = "true",
            ["transport"] = "LongPolling",
            ["messagePack"] = "true",
            ["reconnect"] = "true",
            ["reconnectInterval"] = "10000",
            ["maxReconnectAttempts"] = "5",
            ["targetType"] = "Group",
            ["targetGroup"] = "room1",
            ["ssl"] = "true",
            ["sslCertPath"] = "/cert.pfx",
            ["sslCertPassword"] = "secret",
            ["accessToken"] = "jwt-token",
            ["bridge"] = "false"
        };

        opts.BindFromUri(parameters);

        opts.Mode.Should().Be(SignalRMode.Server);
        opts.Method.Should().Be("SendMessage");
        opts.DefaultGroup.Should().Be("lobby");
        opts.InOut.Should().BeTrue();
        opts.Transport.Should().Be(SignalRTransport.LongPolling);
        opts.MessagePack.Should().BeTrue();
        opts.Reconnect.Should().BeTrue();
        opts.ReconnectInterval.Should().Be(10000);
        opts.MaxReconnectAttempts.Should().Be(5);
        opts.TargetType.Should().Be("Group");
        opts.TargetGroup.Should().Be("room1");
        opts.Ssl.Should().BeTrue();
        opts.SslCertPath.Should().Be("/cert.pfx");
        opts.SslCertPassword.Should().Be("secret");
        opts.AccessToken.Should().Be("jwt-token");
        opts.Bridge.Should().BeFalse();
    }
}

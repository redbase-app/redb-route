using redb.Route.WebSocket;

namespace redb.Route.Tests.WebSocket;

public class WsEndpointOptionsTests
{
    [Fact]
    public void Defaults_AreCorrect()
    {
        var opts = new WsEndpointOptions();
        opts.Host.Should().Be("0.0.0.0");
        opts.Port.Should().Be(8080);
        opts.MessageType.Should().Be(WsMessageType.Text);
        opts.Encoding.Should().Be("utf-8");
        opts.SubProtocol.Should().BeNull();
        opts.ReceiveBufferSize.Should().Be(8192);
        opts.SendBufferSize.Should().Be(8192);
        opts.KeepAliveInterval.Should().Be(30_000);
        opts.ConnectTimeout.Should().Be(10_000);
        opts.Reconnect.Should().BeFalse();
        opts.ReconnectInterval.Should().Be(5_000);
        opts.MaxReconnectAttempts.Should().Be(0);
        opts.MaxConnections.Should().Be(0);
        opts.InOut.Should().BeFalse();
        opts.Ssl.Should().BeFalse();
        opts.SslCertPath.Should().BeNull();
        opts.SslCertPassword.Should().BeNull();
    }

    [Fact]
    public void Validate_ValidOptions_NoException()
    {
        var opts = new WsEndpointOptions { Port = 9000 };
        var act = () => opts.Validate();
        act.Should().NotThrow();
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(70000)]
    public void Validate_InvalidPort_Throws(int port)
    {
        var opts = new WsEndpointOptions { Port = port };
        var act = () => opts.Validate();
        act.Should().Throw<ArgumentException>().WithMessage("*Port*");
    }

    [Fact]
    public void Validate_InvalidReceiveBufferSize_Throws()
    {
        var opts = new WsEndpointOptions { ReceiveBufferSize = 0 };
        var act = () => opts.Validate();
        act.Should().Throw<ArgumentException>().WithMessage("*ReceiveBufferSize*");
    }

    [Fact]
    public void Validate_InvalidSendBufferSize_Throws()
    {
        var opts = new WsEndpointOptions { SendBufferSize = -1 };
        var act = () => opts.Validate();
        act.Should().Throw<ArgumentException>().WithMessage("*SendBufferSize*");
    }

    [Fact]
    public void Validate_NegativeKeepAliveInterval_Throws()
    {
        var opts = new WsEndpointOptions { KeepAliveInterval = -1 };
        var act = () => opts.Validate();
        act.Should().Throw<ArgumentException>().WithMessage("*KeepAliveInterval*");
    }

    [Fact]
    public void Validate_NegativeConnectTimeout_Throws()
    {
        var opts = new WsEndpointOptions { ConnectTimeout = -1 };
        var act = () => opts.Validate();
        act.Should().Throw<ArgumentException>().WithMessage("*ConnectTimeout*");
    }

    [Fact]
    public void Validate_InvalidReconnectInterval_Throws()
    {
        var opts = new WsEndpointOptions { ReconnectInterval = 0 };
        var act = () => opts.Validate();
        act.Should().Throw<ArgumentException>().WithMessage("*ReconnectInterval*");
    }

    [Fact]
    public void Validate_NegativeMaxReconnectAttempts_Throws()
    {
        var opts = new WsEndpointOptions { MaxReconnectAttempts = -1 };
        var act = () => opts.Validate();
        act.Should().Throw<ArgumentException>().WithMessage("*MaxReconnectAttempts*");
    }

    [Fact]
    public void Validate_NegativeMaxConnections_Throws()
    {
        var opts = new WsEndpointOptions { MaxConnections = -1 };
        var act = () => opts.Validate();
        act.Should().Throw<ArgumentException>().WithMessage("*MaxConnections*");
    }

    [Fact]
    public void Validate_ZeroKeepAlive_Valid()
    {
        var opts = new WsEndpointOptions { KeepAliveInterval = 0 };
        var act = () => opts.Validate();
        act.Should().NotThrow();
    }

    [Fact]
    public void Validate_ZeroConnectTimeout_Valid()
    {
        var opts = new WsEndpointOptions { ConnectTimeout = 0 };
        var act = () => opts.Validate();
        act.Should().NotThrow();
    }

    [Fact]
    public void Validate_Port65535_Valid()
    {
        var opts = new WsEndpointOptions { Port = 65535 };
        var act = () => opts.Validate();
        act.Should().NotThrow();
    }

    [Fact]
    public void MessageType_Binary_Accepted()
    {
        var opts = new WsEndpointOptions { MessageType = WsMessageType.Binary };
        opts.MessageType.Should().Be(WsMessageType.Binary);
    }
}

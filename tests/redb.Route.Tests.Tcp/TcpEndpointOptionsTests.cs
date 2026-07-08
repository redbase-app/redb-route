using redb.Route.Tcp;

namespace redb.Route.Tests.Tcp;

public class TcpEndpointOptionsTests
{
    [Fact]
    public void Defaults_AreCorrect()
    {
        var options = new TcpEndpointOptions();

        options.Host.Should().Be("0.0.0.0");
        options.Port.Should().Be(0);
        options.Framing.Should().Be(TcpFraming.Raw);
        options.TextLine.Should().BeFalse();
        options.LengthPrefixed.Should().BeFalse();
        options.Delimiter.Should().Be("\n");
        options.Encoding.Should().Be("utf-8");
        options.KeepAlive.Should().BeTrue();
        options.NoDelay.Should().BeTrue();
        options.ReceiveBufferSize.Should().Be(8192);
        options.SendBufferSize.Should().Be(8192);
        options.ConnectTimeout.Should().Be(10_000);
        options.Reconnect.Should().BeFalse();
        options.ReconnectInterval.Should().Be(5_000);
        options.MaxReconnectAttempts.Should().Be(0);
        options.Backlog.Should().Be(128);
        options.MaxConnections.Should().Be(0);
        options.InOut.Should().BeFalse();
        options.Ssl.Should().BeFalse();
    }

    [Fact]
    public void Validate_ValidOptions_DoesNotThrow()
    {
        var options = new TcpEndpointOptions { Port = 9090 };
        var act = () => options.Validate();
        act.Should().NotThrow();
    }

    [Fact]
    public void Validate_InvalidPort_Negative_Throws()
    {
        var options = new TcpEndpointOptions { Port = -1 };
        var act = () => options.Validate();
        act.Should().Throw<ArgumentException>().WithMessage("*Port*");
    }

    [Fact]
    public void Validate_InvalidPort_TooHigh_Throws()
    {
        var options = new TcpEndpointOptions { Port = 70000 };
        var act = () => options.Validate();
        act.Should().Throw<ArgumentException>().WithMessage("*Port*");
    }

    [Fact]
    public void Validate_InvalidReceiveBufferSize_Throws()
    {
        var options = new TcpEndpointOptions { ReceiveBufferSize = 0 };
        var act = () => options.Validate();
        act.Should().Throw<ArgumentException>().WithMessage("*ReceiveBufferSize*");
    }

    [Fact]
    public void Validate_InvalidSendBufferSize_Throws()
    {
        var options = new TcpEndpointOptions { SendBufferSize = -1 };
        var act = () => options.Validate();
        act.Should().Throw<ArgumentException>().WithMessage("*SendBufferSize*");
    }

    [Fact]
    public void Validate_NegativeConnectTimeout_Throws()
    {
        var options = new TcpEndpointOptions { ConnectTimeout = -1 };
        var act = () => options.Validate();
        act.Should().Throw<ArgumentException>().WithMessage("*ConnectTimeout*");
    }

    [Fact]
    public void Validate_InvalidReconnectInterval_Throws()
    {
        var options = new TcpEndpointOptions { ReconnectInterval = 0 };
        var act = () => options.Validate();
        act.Should().Throw<ArgumentException>().WithMessage("*ReconnectInterval*");
    }

    [Fact]
    public void Validate_NegativeMaxReconnectAttempts_Throws()
    {
        var options = new TcpEndpointOptions { MaxReconnectAttempts = -1 };
        var act = () => options.Validate();
        act.Should().Throw<ArgumentException>().WithMessage("*MaxReconnectAttempts*");
    }

    [Fact]
    public void Validate_InvalidBacklog_Throws()
    {
        var options = new TcpEndpointOptions { Backlog = 0 };
        var act = () => options.Validate();
        act.Should().Throw<ArgumentException>().WithMessage("*Backlog*");
    }

    [Fact]
    public void Validate_NegativeMaxConnections_Throws()
    {
        var options = new TcpEndpointOptions { MaxConnections = -1 };
        var act = () => options.Validate();
        act.Should().Throw<ArgumentException>().WithMessage("*MaxConnections*");
    }

    [Fact]
    public void Validate_TextLine_SetsFraming()
    {
        var options = new TcpEndpointOptions { TextLine = true };
        options.Validate();
        options.Framing.Should().Be(TcpFraming.TextLine);
    }

    [Fact]
    public void Validate_LengthPrefixed_SetsFraming()
    {
        var options = new TcpEndpointOptions { LengthPrefixed = true };
        options.Validate();
        options.Framing.Should().Be(TcpFraming.LengthPrefixed);
    }

    [Fact]
    public void Validate_BothTextLineAndLengthPrefixed_Throws()
    {
        var options = new TcpEndpointOptions { TextLine = true, LengthPrefixed = true };
        var act = () => options.Validate();
        act.Should().Throw<ArgumentException>().WithMessage("*TextLine*LengthPrefixed*");
    }

    [Fact]
    public void Validate_BoundaryValues_AllValid()
    {
        var options = new TcpEndpointOptions
        {
            Port = 0,
            ReceiveBufferSize = 1,
            SendBufferSize = 1,
            ConnectTimeout = 0,
            ReconnectInterval = 1,
            MaxReconnectAttempts = 0,
            Backlog = 1,
            MaxConnections = 0
        };
        var act = () => options.Validate();
        act.Should().NotThrow();
    }

    [Fact]
    public void Validate_Port65535_Valid()
    {
        var options = new TcpEndpointOptions { Port = 65535 };
        var act = () => options.Validate();
        act.Should().NotThrow();
    }
}

using redb.Route.Mail;

namespace redb.Route.Tests.Mail;

public sealed class MailEndpointOptionsTests
{
    [Fact]
    public void Defaults_AreCorrect()
    {
        var opts = new MailEndpointOptions();

        opts.Folder.Should().Be("INBOX");
        opts.Delay.Should().Be(60_000);
        opts.FetchFilter.Should().Be(MailFetchFilter.Unseen);
        opts.MaxMessages.Should().Be(50);
        opts.PostProcess.Should().Be(PostProcessAction.MarkRead);
        opts.IdleTimeout.Should().Be(29 * 60 * 1000);
        opts.Idle.Should().BeFalse();
        opts.Peek.Should().BeFalse();
        opts.Idempotent.Should().BeFalse();
        opts.FetchBody.Should().BeTrue();
        opts.Disconnect.Should().BeFalse();
        opts.KeepRawMessage.Should().BeFalse();
        opts.ConnectionTimeout.Should().Be(30_000);
        opts.Timeout.Should().Be(60_000);
    }

    // ── Port resolution ───────────────────────────────────────────────

    [Theory]
    [InlineData("smtp", MailSecurityMode.Auto, 587)]
    [InlineData("smtp", MailSecurityMode.Ssl, 465)]
    [InlineData("smtp", MailSecurityMode.None, 25)]
    [InlineData("smtp", MailSecurityMode.StartTls, 587)]
    [InlineData("imap", MailSecurityMode.Auto, 993)]
    [InlineData("imap", MailSecurityMode.None, 143)]
    [InlineData("imap", MailSecurityMode.Ssl, 993)]
    [InlineData("pop3", MailSecurityMode.Auto, 995)]
    [InlineData("pop3", MailSecurityMode.None, 110)]
    [InlineData("pop3", MailSecurityMode.Ssl, 995)]
    public void ResolvePort_ReturnsCorrectDefault(string scheme, MailSecurityMode security, int expected)
    {
        var opts = new MailEndpointOptions { Security = security };
        opts.ResolvePort(scheme).Should().Be(expected);
    }

    [Fact]
    public void ResolvePort_ExplicitPortTakesPriority()
    {
        var opts = new MailEndpointOptions { Port = 2525 };
        opts.ResolvePort("smtp").Should().Be(2525);
    }

    // ── Security options ──────────────────────────────────────────────

    [Theory]
    [InlineData(MailSecurityMode.None, MailKit.Security.SecureSocketOptions.None)]
    [InlineData(MailSecurityMode.Ssl, MailKit.Security.SecureSocketOptions.SslOnConnect)]
    [InlineData(MailSecurityMode.StartTls, MailKit.Security.SecureSocketOptions.StartTls)]
    [InlineData(MailSecurityMode.Auto, MailKit.Security.SecureSocketOptions.Auto)]
    public void ResolveSecurityOptions_MapsCorrectly(MailSecurityMode mode, MailKit.Security.SecureSocketOptions expected)
    {
        var opts = new MailEndpointOptions { Security = mode };
        opts.ResolveSecurityOptions().Should().Be(expected);
    }

    // ── Validation ────────────────────────────────────────────────────

    [Fact]
    public void Validate_NegativePort_Throws()
    {
        var opts = new MailEndpointOptions { Port = -1 };
        var act = () => opts.Validate();
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Validate_PortAboveMax_Throws()
    {
        var opts = new MailEndpointOptions { Port = 70_000 };
        var act = () => opts.Validate();
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Validate_NegativeDelay_Throws()
    {
        var opts = new MailEndpointOptions { Delay = -1 };
        var act = () => opts.Validate();
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Validate_NegativeMaxMessages_Throws()
    {
        var opts = new MailEndpointOptions { MaxMessages = -5 };
        var act = () => opts.Validate();
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Validate_MoveWithoutMoveTo_Throws()
    {
        var opts = new MailEndpointOptions { PostProcess = PostProcessAction.Move };
        var act = () => opts.Validate();
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Validate_MarkReadAndMoveWithoutMoveTo_Throws()
    {
        var opts = new MailEndpointOptions { PostProcess = PostProcessAction.MarkReadAndMove };
        var act = () => opts.Validate();
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Validate_MoveWithMoveTo_Succeeds()
    {
        var opts = new MailEndpointOptions
        {
            PostProcess = PostProcessAction.Move,
            MoveTo = "Archive"
        };
        var act = () => opts.Validate();
        act.Should().NotThrow();
    }

    [Fact]
    public void Validate_NegativeConnectionTimeout_Throws()
    {
        var opts = new MailEndpointOptions { ConnectionTimeout = -1 };
        var act = () => opts.Validate();
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Validate_NegativeTimeout_Throws()
    {
        var opts = new MailEndpointOptions { Timeout = -1 };
        var act = () => opts.Validate();
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Validate_DefaultOptions_Succeeds()
    {
        var opts = new MailEndpointOptions();
        var act = () => opts.Validate();
        act.Should().NotThrow();
    }
}

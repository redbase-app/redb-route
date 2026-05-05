using redb.Route.Core;
using redb.Route.Mail;

namespace redb.Route.Tests.Mail;

public class MailBuilderTests
{
    // ── Factories ───────────────────────────────────────────────────

    [Fact]
    public void SmtpSend_StartsWithSmtpScheme()
    {
        var uri = Smtp.Send("smtp.example.com").Build();
        uri.Should().StartWith("smtp:smtp.example.com");
    }

    [Fact]
    public void ImapRead_StartsWithImapScheme()
    {
        var uri = Imap.Read("imap.example.com").Build();
        uri.Should().StartWith("imap:imap.example.com");
    }

    [Fact]
    public void Pop3Read_StartsWithPop3Scheme()
    {
        var uri = Pop3.Read("pop3.example.com").Build();
        uri.Should().StartWith("pop3:pop3.example.com");
    }

    [Fact]
    public void NullServer_Throws()
    {
        var act = () => Smtp.Send(null!);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void EmptyServer_Throws()
    {
        var act = () => Imap.Read("");
        act.Should().Throw<ArgumentException>();
    }

    // ── Connection ──────────────────────────────────────────────────

    [Fact]
    public void Port_SetsParam()
    {
        var uri = Smtp.Send("s").Port(587).Build();
        uri.Should().Contain("port=587");
    }

    [Fact]
    public void Security_SetsParam()
    {
        var uri = Smtp.Send("s").Security("StartTls").Build();
        uri.Should().Contain("security=StartTls");
    }

    [Fact]
    public void Username_SetsParam()
    {
        var uri = Smtp.Send("s").Username("user").Build();
        uri.Should().Contain("username=user");
    }

    [Fact]
    public void Password_SetsParam()
    {
        var uri = Smtp.Send("s").Password("pw").Build();
        uri.Should().Contain("password=pw");
    }

    [Fact]
    public void AccessToken_SetsParam()
    {
        var uri = Smtp.Send("s").AccessToken("tok123").Build();
        uri.Should().Contain("accessToken=tok123");
    }

    [Fact]
    public void AuthMechanism_SetsParam()
    {
        var uri = Smtp.Send("s").AuthMechanism("XOAuth2").Build();
        uri.Should().Contain("authMechanism=XOAuth2");
    }

    [Fact]
    public void SkipCertificateValidation_SetsParam()
    {
        var uri = Smtp.Send("s").SkipCertificateValidation().Build();
        uri.Should().Contain("skipCertificateValidation=true");
    }

    // ── SMTP producer ───────────────────────────────────────────────

    [Fact]
    public void SmtpFrom_SetsParam()
    {
        var uri = Smtp.Send("s").From("a@b.com").Build();
        uri.Should().Contain("from=a%40b.com");
    }

    [Fact]
    public void SmtpTo_SetsParam()
    {
        var uri = Smtp.Send("s").To("x@y.com").Build();
        uri.Should().Contain("to=x%40y.com");
    }

    [Fact]
    public void SmtpCc_SetsParam()
    {
        var uri = Smtp.Send("s").Cc("cc@t.com").Build();
        uri.Should().Contain("cc=cc%40t.com");
    }

    [Fact]
    public void SmtpSubject_SetsParam()
    {
        var uri = Smtp.Send("s").Subject("Alert").Build();
        uri.Should().Contain("subject=Alert");
    }

    [Fact]
    public void SmtpContentType_SetsParam()
    {
        var uri = Smtp.Send("s").ContentType("text/html").Build();
        uri.Should().Contain("contentType=");
    }

    [Fact]
    public void SmtpAlternativeBody_SetsParam()
    {
        var uri = Smtp.Send("s").AlternativeBody().Build();
        uri.Should().Contain("alternativeBody=true");
    }

    // ── IMAP consumer ───────────────────────────────────────────────

    [Fact]
    public void Folder_SetsParam()
    {
        var uri = Imap.Read("s").Folder("INBOX").Build();
        uri.Should().Contain("folder=INBOX");
    }

    [Fact]
    public void Delay_SetsParam()
    {
        var uri = Imap.Read("s").Delay(30000).Build();
        uri.Should().Contain("delay=30000");
    }

    [Fact]
    public void Unseen_SetsFilter()
    {
        var uri = Imap.Read("s").Unseen().Build();
        uri.Should().Contain("fetchFilter=Unseen");
    }

    [Fact]
    public void MaxMessages_SetsParam()
    {
        var uri = Imap.Read("s").MaxMessages(10).Build();
        uri.Should().Contain("maxMessages=10");
    }

    [Fact]
    public void PostProcess_SetsParam()
    {
        var uri = Imap.Read("s").PostProcess("MarkRead").Build();
        uri.Should().Contain("postProcess=MarkRead");
    }

    [Fact]
    public void Idle_SetsParam()
    {
        var uri = Imap.Read("s").Idle().Build();
        uri.Should().Contain("idle=true");
    }

    [Fact]
    public void Idempotent_SetsParam()
    {
        var uri = Imap.Read("s").Idempotent().Build();
        uri.Should().Contain("idempotent=true");
    }

    // ── Conversion ──────────────────────────────────────────────────

    [Fact]
    public void ImplicitConversion_ReturnsUri()
    {
        string uri = Smtp.Send("s").Port(587).Security("StartTls");
        uri.Should().StartWith("smtp:s?");
    }

    [Fact]
    public void ToString_ReturnsSameAsBuild()
    {
        var builder = Imap.Read("s").Folder("INBOX").Unseen();
        builder.ToString().Should().Be(builder.Build());
    }

    // ── Full chains ─────────────────────────────────────────────────

    [Fact]
    public void FullChain_Smtp_BuildsCorrectUri()
    {
        var uri = Smtp.Send("smtp.example.com")
            .Port(587)
            .Security("StartTls")
            .Username("bot@example.com")
            .Password("secret")
            .From("bot@example.com")
            .To("user@example.com")
            .Subject("Alert")
            .Build();

        uri.Should().StartWith("smtp:smtp.example.com?");
        uri.Should().Contain("port=587");
        uri.Should().Contain("security=StartTls");
        uri.Should().Contain("subject=Alert");
    }

    [Fact]
    public void FullChain_Imap_BuildsCorrectUri()
    {
        var uri = Imap.Read("imap.example.com")
            .Port(993)
            .Security("Ssl")
            .Username("inbox@example.com")
            .Password("secret")
            .Folder("INBOX")
            .Unseen()
            .Delay(30000)
            .MaxMessages(50)
            .Build();

        uri.Should().StartWith("imap:imap.example.com?");
        uri.Should().Contain("port=993");
        uri.Should().Contain("security=Ssl");
        uri.Should().Contain("folder=INBOX");
        uri.Should().Contain("fetchFilter=Unseen");
    }

    // ── Round-trip ──────────────────────────────────────────────────

    [Fact]
    public void RoundTrip_Smtp_ParseAndReconstruct()
    {
        var original = Smtp.Send("smtp.example.com").Port(587).Build();
        var parsed = EndpointUriParser.Parse(original);
        parsed.Scheme.Should().Be("smtp");
        parsed.Path.Should().Be("smtp.example.com");
        parsed.RawParameters["port"].Should().Be("587");
    }

    [Fact]
    public void RoundTrip_Imap_ParseAndReconstruct()
    {
        var original = Imap.Read("imap.example.com").Folder("INBOX").Build();
        var parsed = EndpointUriParser.Parse(original);
        parsed.Scheme.Should().Be("imap");
        parsed.Path.Should().Be("imap.example.com");
        parsed.RawParameters["folder"].Should().Be("INBOX");
    }
}

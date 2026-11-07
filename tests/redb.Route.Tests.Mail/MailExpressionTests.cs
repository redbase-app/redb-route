using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Expressions;
using redb.Route.Mail;

namespace redb.Route.Tests.Mail;

/// <summary>
/// Tests for expression resolution in Mail producer options (Phase 1).
/// All 7 properties (From, To, Cc, Bcc, ReplyTo, Subject, ContentType) are string,
/// so expression support is purely via ResolveOption — no numeric *Expression properties needed.
/// </summary>
public class MailExpressionTests
{
    // ── ResolveOption: each string property resolves expressions ─────

    [Fact]
    public void ResolveOption_From_ResolvesExpression()
    {
        var opts = new MailEndpointOptions { From = "${header.sender}" };
        var msg = new Message("body");
        msg.Headers["sender"] = "alice@example.com";
        var exchange = new Exchange(msg);

        opts.ResolveOption(opts.From, exchange).Should().Be("alice@example.com");
    }

    [Fact]
    public void ResolveOption_To_ResolvesExpression()
    {
        var opts = new MailEndpointOptions { To = "${header.recipient}" };
        var msg = new Message("body");
        msg.Headers["recipient"] = "bob@example.com";
        var exchange = new Exchange(msg);

        opts.ResolveOption(opts.To, exchange).Should().Be("bob@example.com");
    }

    [Fact]
    public void ResolveOption_Cc_ResolvesExpression()
    {
        var opts = new MailEndpointOptions { Cc = "${header.cc}" };
        var msg = new Message("body");
        msg.Headers["cc"] = "cc@example.com";
        var exchange = new Exchange(msg);

        opts.ResolveOption(opts.Cc, exchange).Should().Be("cc@example.com");
    }

    [Fact]
    public void ResolveOption_Bcc_ResolvesExpression()
    {
        var opts = new MailEndpointOptions { Bcc = "${header.bcc}" };
        var msg = new Message("body");
        msg.Headers["bcc"] = "bcc@example.com";
        var exchange = new Exchange(msg);

        opts.ResolveOption(opts.Bcc, exchange).Should().Be("bcc@example.com");
    }

    [Fact]
    public void ResolveOption_ReplyTo_ResolvesExpression()
    {
        var opts = new MailEndpointOptions { ReplyTo = "${header.reply}" };
        var msg = new Message("body");
        msg.Headers["reply"] = "reply@example.com";
        var exchange = new Exchange(msg);

        opts.ResolveOption(opts.ReplyTo, exchange).Should().Be("reply@example.com");
    }

    [Fact]
    public void ResolveOption_Subject_ResolvesExpression()
    {
        var opts = new MailEndpointOptions { Subject = "Order ${header.orderId}" };
        var msg = new Message("body");
        msg.Headers["orderId"] = "42";
        var exchange = new Exchange(msg);

        opts.ResolveOption(opts.Subject, exchange).Should().Be("Order 42");
    }

    [Fact]
    public void ResolveOption_ContentType_ResolvesExpression()
    {
        var opts = new MailEndpointOptions { ContentType = "${header.ct}" };
        var msg = new Message("body");
        msg.Headers["ct"] = "text/html";
        var exchange = new Exchange(msg);

        opts.ResolveOption(opts.ContentType, exchange).Should().Be("text/html");
    }

    // ── Static values pass through unchanged ────────────────────────

    [Fact]
    public void ResolveOption_StaticSubject_ReturnsAsIs()
    {
        var opts = new MailEndpointOptions { Subject = "Welcome!" };
        var exchange = new Exchange(new Message("body"));

        opts.ResolveOption(opts.Subject, exchange).Should().Be("Welcome!");
    }

    [Fact]
    public void ResolveOption_EmptyString_ReturnsEmpty()
    {
        var opts = new MailEndpointOptions(); // defaults are ""
        var exchange = new Exchange(new Message("body"));

        opts.ResolveOption(opts.From, exchange).Should().Be("");
    }

    // ── DSL: IExpression overloads produce template strings ──────────

    [Fact]
    public void DslBuild_FromExpression_StoresTemplateString()
    {
        var uri = Smtp.Send("smtp.example.com")
            .From(new HeaderExpression("sender"))
            .Build();

        uri.Should().Contain("from=");
    }

    [Fact]
    public void DslBuild_ToExpression_StoresTemplateString()
    {
        var uri = Smtp.Send("smtp.example.com")
            .To(new HeaderExpression("recipient"))
            .Build();

        uri.Should().Contain("to=");
    }

    [Fact]
    public void DslBuild_SubjectExpression_StoresTemplateString()
    {
        var uri = Smtp.Send("smtp.example.com")
            .Subject(new HeaderExpression("subj"))
            .Build();

        uri.Should().Contain("subject=");
    }

    [Fact]
    public void DslBuild_ContentTypeExpression_StoresTemplateString()
    {
        var uri = Smtp.Send("smtp.example.com")
            .ContentType(new HeaderExpression("ct"))
            .Build();

        uri.Should().Contain("contentType=");
    }

    [Fact]
    public void DslBuild_CcExpression_StoresTemplateString()
    {
        var uri = Smtp.Send("smtp.example.com")
            .Cc(new HeaderExpression("cc"))
            .Build();

        uri.Should().Contain("cc=");
    }

    [Fact]
    public void DslBuild_BccExpression_StoresTemplateString()
    {
        var uri = Smtp.Send("smtp.example.com")
            .Bcc(new HeaderExpression("bcc"))
            .Build();

        uri.Should().Contain("bcc=");
    }

    [Fact]
    public void DslBuild_ReplyToExpression_StoresTemplateString()
    {
        var uri = Smtp.Send("smtp.example.com")
            .ReplyTo(new HeaderExpression("reply"))
            .Build();

        uri.Should().Contain("replyTo=");
    }
}

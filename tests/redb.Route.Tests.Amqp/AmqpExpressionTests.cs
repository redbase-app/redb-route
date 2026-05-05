using redb.Route.Abstractions;
using redb.Route.Amqp;
using redb.Route.Core;
using redb.Route.Expressions;
using AmqpDsl = redb.Route.Amqp.Amqp;

namespace redb.Route.Tests.Amqp;

/// <summary>
/// Tests for expression resolution in AMQP producer options (Phase 1).
/// Verifies that expression properties bind correctly and resolve at runtime.
/// </summary>
public class AmqpExpressionTests
{
    // ── BindFromUri: expression binds to *Expression property ────────

    [Fact]
    public void BindFromUri_MessagePriorityExpression_BindsCorrectly()
    {
        var opts = new AmqpEndpointOptions();
        opts.BindFromUri(new Dictionary<string, string>
        {
            ["host"] = "localhost",
            ["messagePriorityExpression"] = "${header.prio}"
        });

        opts.MessagePriorityExpression.Should().Be("${header.prio}");
        opts.MessagePriority.Should().Be(4); // default unchanged
    }

    [Fact]
    public void BindFromUri_MessageTtlExpression_BindsCorrectly()
    {
        var opts = new AmqpEndpointOptions();
        opts.BindFromUri(new Dictionary<string, string>
        {
            ["host"] = "localhost",
            ["messageTtlExpression"] = "${header.ttl}"
        });

        opts.MessageTtlExpression.Should().Be("${header.ttl}");
        opts.MessageTtl.Should().Be(0); // default unchanged
    }

    // ── ResolveOption: string properties resolve expressions ─────────

    [Fact]
    public void ResolveOption_Subject_ResolvesExpression()
    {
        var opts = new AmqpEndpointOptions { Subject = "order-${header.orderId}" };
        var msg = new Message("body");
        msg.Headers["orderId"] = "12345";
        var exchange = new Exchange(msg);

        opts.ResolveOption(opts.Subject, exchange).Should().Be("order-12345");
    }

    [Fact]
    public void ResolveOption_GroupId_ResolvesExpression()
    {
        var opts = new AmqpEndpointOptions { GroupId = "group-${header.region}" };
        var msg = new Message("body");
        msg.Headers["region"] = "eu-west";
        var exchange = new Exchange(msg);

        opts.ResolveOption(opts.GroupId, exchange).Should().Be("group-eu-west");
    }

    [Fact]
    public void ResolveOption_ContentType_ResolvesExpression()
    {
        var opts = new AmqpEndpointOptions { ContentType = "${header.ct}" };
        var msg = new Message("body");
        msg.Headers["ct"] = "application/json";
        var exchange = new Exchange(msg);

        opts.ResolveOption(opts.ContentType, exchange).Should().Be("application/json");
    }

    [Fact]
    public void ResolveOption_Subject_StaticValue_ReturnsAsIs()
    {
        var opts = new AmqpEndpointOptions { Subject = "static-subject" };
        var exchange = new Exchange(new Message("body"));

        opts.ResolveOption(opts.Subject, exchange).Should().Be("static-subject");
    }

    // ── ResolveOption: numeric expressions resolve then TryParse ─────

    [Fact]
    public void ResolveOption_MessagePriorityExpression_ResolvesFromExchange()
    {
        var opts = new AmqpEndpointOptions { MessagePriorityExpression = "${header.prio}" };
        var msg = new Message("body");
        msg.Headers["prio"] = "7";
        var exchange = new Exchange(msg);

        var resolved = opts.ResolveOption(opts.MessagePriorityExpression, exchange);
        resolved.Should().Be("7");
        byte.TryParse(resolved, out var prio).Should().BeTrue();
        prio.Should().Be(7);
    }

    [Fact]
    public void ResolveOption_MessageTtlExpression_ResolvesFromExchange()
    {
        var opts = new AmqpEndpointOptions { MessageTtlExpression = "${header.ttl}" };
        var msg = new Message("body");
        msg.Headers["ttl"] = "60000";
        var exchange = new Exchange(msg);

        var resolved = opts.ResolveOption(opts.MessageTtlExpression, exchange);
        resolved.Should().Be("60000");
        uint.TryParse(resolved, out var ttl).Should().BeTrue();
        ttl.Should().Be(60000u);
    }

    // ── DSL: expression routing in Build ─────────────────────────────

    [Fact]
    public void DslBuild_MessagePriorityExpression_RoutesToExpressionParam()
    {
        var uri = AmqpDsl.Address("test-queue")
            .Host("localhost")
            .MessagePriority(new HeaderExpression("prio"))
            .Build();

        uri.Should().Contain("messagePriorityExpression=");
        uri.Should().NotContain("&messagePriority=");
    }

    [Fact]
    public void DslBuild_MessagePriorityStatic_RoutesToNormalParam()
    {
        var uri = AmqpDsl.Address("test-queue")
            .Host("localhost")
            .MessagePriority(7)
            .Build();

        uri.Should().Contain("messagePriority=7");
        uri.Should().NotContain("messagePriorityExpression");
    }

    [Fact]
    public void DslBuild_MessageTtlExpression_RoutesToExpressionParam()
    {
        var uri = AmqpDsl.Address("test-queue")
            .Host("localhost")
            .MessageTtl(new HeaderExpression("ttl"))
            .Build();

        uri.Should().Contain("messageTtlExpression=");
        uri.Should().NotContain("&messageTtl=");
    }

    [Fact]
    public void DslBuild_SubjectExpression_StoresTemplateString()
    {
        var uri = AmqpDsl.Address("test-queue")
            .Host("localhost")
            .Subject(new HeaderExpression("subj"))
            .Build();

        uri.Should().Contain("subject=");
    }

    [Fact]
    public void DslBuild_GroupIdExpression_StoresTemplateString()
    {
        var uri = AmqpDsl.Address("test-queue")
            .Host("localhost")
            .GroupId(new HeaderExpression("grp"))
            .Build();

        uri.Should().Contain("groupId=");
    }

    [Fact]
    public void DslBuild_ContentTypeExpression_StoresTemplateString()
    {
        var uri = AmqpDsl.Address("test-queue")
            .Host("localhost")
            .ContentType(new HeaderExpression("ct"))
            .Build();

        uri.Should().Contain("contentType=");
    }
}

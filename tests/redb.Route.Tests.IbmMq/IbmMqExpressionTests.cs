using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Expressions;
using redb.Route.IbmMq;
using IbmMqDsl = redb.Route.IbmMq.Wmq;

namespace redb.Route.Tests.IbmMq;

/// <summary>
/// Tests for expression resolution in IBM MQ producer options (Phase 2).
/// Verifies that expression properties bind correctly and resolve at runtime.
/// </summary>
public class IbmMqExpressionTests
{
    // ── BindFromUri: expression binds to *Expression property ────────

    [Fact]
    public void BindFromUri_PersistenceExpression_BindsCorrectly()
    {
        var opts = new IbmMqEndpointOptions();
        opts.BindFromUri(new Dictionary<string, string>
        {
            ["host"] = "localhost",
            ["persistenceExpression"] = "${header.pers}"
        });

        opts.PersistenceExpression.Should().Be("${header.pers}");
        opts.Persistence.Should().Be(IbmMqPersistence.AsQDef); // default unchanged
    }

    [Fact]
    public void BindFromUri_PriorityExpression_BindsCorrectly()
    {
        var opts = new IbmMqEndpointOptions();
        opts.BindFromUri(new Dictionary<string, string>
        {
            ["host"] = "localhost",
            ["priorityExpression"] = "${header.prio}"
        });

        opts.PriorityExpression.Should().Be("${header.prio}");
        opts.Priority.Should().Be(-1); // default unchanged
    }

    [Fact]
    public void BindFromUri_ExpiryExpression_BindsCorrectly()
    {
        var opts = new IbmMqEndpointOptions();
        opts.BindFromUri(new Dictionary<string, string>
        {
            ["host"] = "localhost",
            ["expiryExpression"] = "${header.exp}"
        });

        opts.ExpiryExpression.Should().Be("${header.exp}");
        opts.Expiry.Should().Be(-1); // default unchanged
    }

    [Fact]
    public void BindFromUri_MessageTypeExpression_BindsCorrectly()
    {
        var opts = new IbmMqEndpointOptions();
        opts.BindFromUri(new Dictionary<string, string>
        {
            ["host"] = "localhost",
            ["messageTypeExpression"] = "${header.mt}"
        });

        opts.MessageTypeExpression.Should().Be("${header.mt}");
        opts.MessageType.Should().Be(IbmMqMessageType.Datagram); // default unchanged
    }

    // ── ResolveOption: expressions resolve from exchange ─────────────

    [Fact]
    public void ResolveOption_PersistenceExpression_ResolvesByName()
    {
        var opts = new IbmMqEndpointOptions { PersistenceExpression = "${header.pers}" };
        var msg = new Message("body");
        msg.Headers["pers"] = "Persistent";
        var exchange = new Exchange(msg);

        var resolved = opts.ResolveOption(opts.PersistenceExpression, exchange);
        resolved.Should().Be("Persistent");
        Enum.TryParse<IbmMqPersistence>(resolved, true, out var parsed).Should().BeTrue();
        parsed.Should().Be(IbmMqPersistence.Persistent);
    }

    [Fact]
    public void ResolveOption_PersistenceExpression_ResolvesByNumericValue()
    {
        var opts = new IbmMqEndpointOptions { PersistenceExpression = "${header.pers}" };
        var msg = new Message("body");
        msg.Headers["pers"] = "1";
        var exchange = new Exchange(msg);

        var resolved = opts.ResolveOption(opts.PersistenceExpression, exchange);
        resolved.Should().Be("1");
        Enum.TryParse<IbmMqPersistence>(resolved, true, out var parsed).Should().BeTrue();
        parsed.Should().Be(IbmMqPersistence.Persistent);
    }

    [Fact]
    public void ResolveOption_PriorityExpression_ResolvesFromExchange()
    {
        var opts = new IbmMqEndpointOptions { PriorityExpression = "${header.prio}" };
        var msg = new Message("body");
        msg.Headers["prio"] = "5";
        var exchange = new Exchange(msg);

        var resolved = opts.ResolveOption(opts.PriorityExpression, exchange);
        resolved.Should().Be("5");
        int.TryParse(resolved, out var prio).Should().BeTrue();
        prio.Should().Be(5);
    }

    [Fact]
    public void ResolveOption_ExpiryExpression_ResolvesFromExchange()
    {
        var opts = new IbmMqEndpointOptions { ExpiryExpression = "${header.exp}" };
        var msg = new Message("body");
        msg.Headers["exp"] = "3000";
        var exchange = new Exchange(msg);

        var resolved = opts.ResolveOption(opts.ExpiryExpression, exchange);
        resolved.Should().Be("3000");
        int.TryParse(resolved, out var expiry).Should().BeTrue();
        expiry.Should().Be(3000);
    }

    [Fact]
    public void ResolveOption_MessageTypeExpression_ResolvesByName()
    {
        var opts = new IbmMqEndpointOptions { MessageTypeExpression = "${header.mt}" };
        var msg = new Message("body");
        msg.Headers["mt"] = "Request";
        var exchange = new Exchange(msg);

        var resolved = opts.ResolveOption(opts.MessageTypeExpression, exchange);
        resolved.Should().Be("Request");
        Enum.TryParse<IbmMqMessageType>(resolved, true, out var parsed).Should().BeTrue();
        parsed.Should().Be(IbmMqMessageType.Request);
    }

    [Fact]
    public void ResolveOption_MessageTypeExpression_ResolvesByNumericValue()
    {
        var opts = new IbmMqEndpointOptions { MessageTypeExpression = "${header.mt}" };
        var msg = new Message("body");
        msg.Headers["mt"] = "1";
        var exchange = new Exchange(msg);

        var resolved = opts.ResolveOption(opts.MessageTypeExpression, exchange);
        resolved.Should().Be("1");
        Enum.TryParse<IbmMqMessageType>(resolved, true, out var parsed).Should().BeTrue();
        parsed.Should().Be(IbmMqMessageType.Request);
    }

    [Fact]
    public void ResolveOption_StaticValue_ReturnsAsIs()
    {
        var opts = new IbmMqEndpointOptions();
        var exchange = new Exchange(new Message("body"));

        opts.ResolveOption("static-value", exchange).Should().Be("static-value");
    }

    // ── DSL: expression routing in Build ─────────────────────────────

    [Fact]
    public void DslBuild_PersistenceExpression_RoutesToExpressionParam()
    {
        var uri = IbmMqDsl.Queue("DEV.QUEUE.1")
            .Host("localhost")
            .Persistence(new HeaderExpression("pers"))
            .Build();

        uri.Should().Contain("persistenceExpression=");
        uri.Should().NotContain("&persistence=");
    }

    [Fact]
    public void DslBuild_PersistenceStatic_RoutesToEnumParam()
    {
        var uri = IbmMqDsl.Queue("DEV.QUEUE.1")
            .Host("localhost")
            .Persistent()
            .Build();

        uri.Should().Contain("persistence=Persistent");
        uri.Should().NotContain("persistenceExpression");
    }

    [Fact]
    public void DslBuild_PriorityExpression_RoutesToExpressionParam()
    {
        var uri = IbmMqDsl.Queue("DEV.QUEUE.1")
            .Host("localhost")
            .Priority(new HeaderExpression("prio"))
            .Build();

        uri.Should().Contain("priorityExpression=");
        uri.Should().NotContain("&priority=");
    }

    [Fact]
    public void DslBuild_PriorityStatic_RoutesToNormalParam()
    {
        var uri = IbmMqDsl.Queue("DEV.QUEUE.1")
            .Host("localhost")
            .Priority(5)
            .Build();

        uri.Should().Contain("priority=5");
        uri.Should().NotContain("priorityExpression");
    }

    [Fact]
    public void DslBuild_ExpiryExpression_RoutesToExpressionParam()
    {
        var uri = IbmMqDsl.Queue("DEV.QUEUE.1")
            .Host("localhost")
            .Expiry(new HeaderExpression("exp"))
            .Build();

        uri.Should().Contain("expiryExpression=");
        uri.Should().NotContain("&expiry=");
    }

    [Fact]
    public void DslBuild_ExpiryStatic_RoutesToNormalParam()
    {
        var uri = IbmMqDsl.Queue("DEV.QUEUE.1")
            .Host("localhost")
            .Expiry(3000)
            .Build();

        uri.Should().Contain("expiry=3000");
        uri.Should().NotContain("expiryExpression");
    }

    [Fact]
    public void DslBuild_MessageTypeExpression_RoutesToExpressionParam()
    {
        var uri = IbmMqDsl.Queue("DEV.QUEUE.1")
            .Host("localhost")
            .MessageType(new HeaderExpression("mt"))
            .Build();

        uri.Should().Contain("messageTypeExpression=");
        uri.Should().NotContain("&messageType=");
    }

    [Fact]
    public void DslBuild_MessageTypeStatic_RoutesToEnumParam()
    {
        var uri = IbmMqDsl.Queue("DEV.QUEUE.1")
            .Host("localhost")
            .MessageType(IbmMqMessageType.Request)
            .Build();

        uri.Should().Contain("messageType=Request");
        uri.Should().NotContain("messageTypeExpression");
    }
}

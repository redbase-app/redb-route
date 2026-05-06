using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Expressions;
using redb.Route.SignalR;
using SignalRDsl = redb.Route.SignalR.SignalR;

namespace redb.Route.Tests.SignalR;

/// <summary>
/// Tests for expression resolution in SignalR producer options (Phase 2).
/// Verifies that string properties resolve expressions at runtime via ResolveOption.
/// </summary>
public class SignalRExpressionTests
{
    // ── ResolveOption: Method resolves expression ────────────────────

    [Fact]
    public void ResolveOption_Method_ResolvesExpression()
    {
        var opts = new SignalREndpointOptions { Method = "${header.hubMethod}" };
        var msg = new Message("payload");
        msg.Headers["hubMethod"] = "SendNotification";
        var exchange = new Exchange(msg);

        opts.ResolveOption(opts.Method, exchange).Should().Be("SendNotification");
    }

    [Fact]
    public void ResolveOption_Method_StaticValue_ReturnsAsIs()
    {
        var opts = new SignalREndpointOptions { Method = "StaticMethod" };
        var exchange = new Exchange(new Message("body"));

        opts.ResolveOption(opts.Method, exchange).Should().Be("StaticMethod");
    }

    // ── ResolveOption: TargetType resolves expression ────────────────

    [Fact]
    public void ResolveOption_TargetType_ResolvesExpression()
    {
        var opts = new SignalREndpointOptions { TargetType = "${header.target}" };
        var msg = new Message("payload");
        msg.Headers["target"] = "Group";
        var exchange = new Exchange(msg);

        opts.ResolveOption(opts.TargetType, exchange).Should().Be("Group");
    }

    [Fact]
    public void ResolveOption_TargetType_StaticValue_ReturnsAsIs()
    {
        var opts = new SignalREndpointOptions { TargetType = "All" };
        var exchange = new Exchange(new Message("body"));

        opts.ResolveOption(opts.TargetType, exchange).Should().Be("All");
    }

    // ── ResolveOption: TargetGroup resolves expression ───────────────

    [Fact]
    public void ResolveOption_TargetGroup_ResolvesExpression()
    {
        var opts = new SignalREndpointOptions { TargetGroup = "${header.grp}" };
        var msg = new Message("payload");
        msg.Headers["grp"] = "room-42";
        var exchange = new Exchange(msg);

        opts.ResolveOption(opts.TargetGroup, exchange).Should().Be("room-42");
    }

    [Fact]
    public void ResolveOption_TargetGroup_StaticValue_ReturnsAsIs()
    {
        var opts = new SignalREndpointOptions { TargetGroup = "lobby" };
        var exchange = new Exchange(new Message("body"));

        opts.ResolveOption(opts.TargetGroup, exchange).Should().Be("lobby");
    }

    // ── DSL: expression routing in Build ─────────────────────────────

    [Fact]
    public void DslBuild_MethodExpression_StoresTemplateString()
    {
        var uri = SignalRDsl.Connect("localhost:5000/chatHub")
            .Method(new HeaderExpression("hubMethod"))
            .Build();

        uri.Should().Contain("method=");
        // The expression template gets URL-encoded
        uri.Should().Contain("method=%24%7bheader.hubMethod%7d");
    }

    [Fact]
    public void DslBuild_MethodStatic_StoresPlainString()
    {
        var uri = SignalRDsl.Connect("localhost:5000/chatHub")
            .Method("Send")
            .Build();

        uri.Should().Contain("method=Send");
    }

    [Fact]
    public void DslBuild_TargetExpression_StoresTemplateString()
    {
        var uri = SignalRDsl.Broadcast("localhost:5000/chatHub")
            .Target(new HeaderExpression("target"))
            .Build();

        uri.Should().Contain("targetType=");
    }

    [Fact]
    public void DslBuild_GroupExpression_SetsTargetTypeAndGroup()
    {
        var uri = SignalRDsl.Broadcast("localhost:5000/chatHub")
            .Group(new HeaderExpression("grp"))
            .Build();

        uri.Should().Contain("targetType=group");
        uri.Should().Contain("targetGroup=");
    }

    [Fact]
    public void DslBuild_GroupStatic_SetsTargetTypeAndGroup()
    {
        var uri = SignalRDsl.Broadcast("localhost:5000/chatHub")
            .Group("room1")
            .Build();

        uri.Should().Contain("targetType=group");
        uri.Should().Contain("targetGroup=room1");
    }
}

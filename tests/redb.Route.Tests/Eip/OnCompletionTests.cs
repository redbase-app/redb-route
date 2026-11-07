using redb.Route.Core;
using redb.Route.TestKit;

namespace redb.Route.Tests.Eip;

/// <summary>OnCompletion(): after the route, on a copy, outside error handling.</summary>
public class OnCompletionTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(2);

    [Fact]
    public async Task Default_RunsAfterTheRoute_OnACopy_WithoutChangingTheReply()
    {
        await using var ctx = new RouteContext().AddRoutes(b => b
            .From("direct://done")
                .OnCompletion().SetHeader("completed", true).To("mock://done").EndOnCompletion()
                .SetBody("reply"));
        await ctx.Start();

        var reply = await ctx.RequestBody<string>("direct://done", "in");

        reply.Should().Be("reply");
        await ctx.Mock("mock://done").ExpectBodies("reply").ExpectHeader("completed", true).AssertIsSatisfiedAsync(Wait);
    }

    [Fact]
    public async Task OnFailureOnly_SeesTheException_AndSkipsSuccess()
    {
        string? captured = null;
        await using var ctx = new RouteContext().AddRoutes(b => b
            .From("direct://fail")
                .OnCompletion().OnFailureOnly().Process(e => captured = e.Exception?.Message).To("mock://failed").EndOnCompletion()
                .Filter(e => Equals(e.In.Body, "boom")).ThrowException<InvalidOperationException>("boom").EndFilter()
                .To("mock://ok"));
        await ctx.Start();

        var act = () => ctx.SendBody("direct://fail", "boom");
        await act.Should().ThrowAsync<InvalidOperationException>();
        await ctx.Mock("mock://failed").ExpectMessageCount(1).AssertIsSatisfiedAsync(Wait);
        captured.Should().Be("boom");

        await ctx.SendBody("direct://fail", "fine");
        await ctx.Mock("mock://ok").ExpectMessageCount(1).AssertIsSatisfiedAsync(Wait);
        await ctx.Mock("mock://failed").ExpectMessageCount(1).AssertIsSatisfiedAsync(Wait);
    }

    [Fact]
    public async Task OnCompleteOnly_WithWhen()
    {
        await using var ctx = new RouteContext().AddRoutes(b => b
            .From("direct://audit")
                .OnCompletion().OnCompleteOnly().When("header.audit").To("mock://audited").EndOnCompletion()
                .To("mock://out"));
        await ctx.Start();

        await ctx.SendBodyAndHeader("direct://audit", "a", "audit", true);
        await ctx.SendBodyAndHeader("direct://audit", "b", "audit", false);

        await ctx.Mock("mock://out").ExpectMessageCount(2).AssertIsSatisfiedAsync(Wait);
        await ctx.Mock("mock://audited").ExpectBodies("a").AssertIsSatisfiedAsync(Wait);
        await Task.Delay(200);
        ctx.Mock("mock://audited").ReceivedCount.Should().Be(1, "audit=false must not trigger the block, even asynchronously");
    }

    [Fact]
    public async Task ModeBeforeConsumer_RunsBeforeTheCallerGetsTheReply()
    {
        await using var ctx = new RouteContext().AddRoutes(b => b
            .From("direct://sync")
                .OnCompletion().ModeBeforeConsumer().To("mock://sync").EndOnCompletion()
                .SetBody("x"));
        await ctx.Start();

        await ctx.SendBody("direct://sync", "in");

        ctx.Mock("mock://sync").ReceivedCount.Should().Be(1, "no waiting: the block ran before the send returned");
    }

    [Fact]
    public async Task HandledException_IsACompletion_NotAFailure()
    {
        await using var ctx = new RouteContext().AddRoutes(b =>
        {
            b.OnException<InvalidOperationException>().Handled();
            b.From("direct://handled")
                .OnCompletion().OnFailureOnly().ModeBeforeConsumer().To("mock://h-failed").EndOnCompletion()
                .OnCompletion().OnCompleteOnly().ModeBeforeConsumer().To("mock://h-complete").EndOnCompletion()
                .ThrowException<InvalidOperationException>("handled");
        });
        await ctx.Start();

        await ctx.SendBody("direct://handled", "x");

        ctx.Mock("mock://h-complete").ReceivedCount.Should().Be(1);
        ctx.Mock("mock://h-failed").ReceivedCount.Should().Be(0);
    }

    [Fact]
    public async Task CompletionMutations_DoNotLeakIntoTheRouteResult()
    {
        await using var ctx = new RouteContext().AddRoutes(b => b
            .From("direct://copy")
                .OnCompletion().ModeBeforeConsumer().SetBody("mutated").SetHeader("h", "mutated").EndOnCompletion()
                .SetBody("reply"));
        await ctx.Start();

        var reply = await ctx.RequestBodyAndHeaders<string>("direct://copy", "in", new Dictionary<string, object?> { ["h"] = "orig" });

        reply.Should().Be("reply");
    }

    [Fact]
    public async Task BuilderLevel_OnCompletion_AppliesToEveryRoute()
    {
        await using var ctx = new RouteContext().AddRoutes(b =>
        {
            b.OnCompletion().ModeBeforeConsumer().To("mock://all-done");
            b.From("direct://c1").SetBody("1");
            b.From("direct://c2").SetBody("2");
        });
        await ctx.Start();

        await ctx.SendBody("direct://c1", "x");
        await ctx.SendBody("direct://c2", "x");

        await ctx.Mock("mock://all-done").ExpectBodies("1", "2").AssertIsSatisfiedAsync(Wait);
    }

    [Fact]
    public async Task FailingCompletionBlock_IsLogged_NotThrown()
    {
        await using var ctx = new RouteContext().AddRoutes(b => b
            .From("direct://bad-block")
                .OnCompletion().ModeBeforeConsumer().ThrowException<InvalidOperationException>("block failed").EndOnCompletion()
                .SetBody("reply"));
        await ctx.Start();

        (await ctx.RequestBody<string>("direct://bad-block", "in")).Should().Be("reply");
    }
}

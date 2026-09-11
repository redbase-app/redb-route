using redb.Route.Core;
using redb.Route.TestKit;

namespace redb.Route.Tests.TestKit;

/// <summary>NotifyBuilder waits for route outcomes without a mock at the end of the route.</summary>
public class NotifyBuilderTests
{
    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(2);

    [Fact]
    public async Task WhenDone_CountsRouteLevelExchanges_NotSplitFragments()
    {
        await using var ctx = new RouteContext().AddRoutes(b => b
            .From("direct://n-in").RouteId("split")
                .Split(e => (IEnumerable<object?>)e.In.Body!).To("mock://n-items").EndSplit());
        await ctx.Start();
        var done = ctx.Notify().FromRoute("split").WhenDone(3).Create();

        for (var i = 0; i < 3; i++)
            await ctx.SendBody("direct://n-in", new object?[] { "a", "b" });

        (await done.MatchesAsync(Wait)).Should().BeTrue();
        done.ReceivedCount.Should().Be(3);
        done.CompletedCount.Should().Be(3);
        done.FailedCount.Should().Be(0);
        ctx.Mock("mock://n-items").ReceivedCount.Should().Be(6, "the split fan-out itself is untouched");
    }

    [Fact]
    public async Task WhenFailed_CountsExchangesThatEscapeTheRoute()
    {
        await using var ctx = new RouteContext().AddRoutes(b => b
            .From("direct://f-in").RouteId("failing").ThrowException<InvalidOperationException>("x"));
        await ctx.Start();
        var failed = ctx.Notify().FromRoute("failing").WhenFailed(1).Create();

        var act = () => ctx.SendBody("direct://f-in", "a");
        await act.Should().ThrowAsync<InvalidOperationException>();

        (await failed.MatchesAsync(Wait)).Should().BeTrue();
        failed.CompletedCount.Should().Be(0);
        failed.DoneCount.Should().Be(1);
    }

    [Fact]
    public async Task HandledException_CountsAsCompleted()
    {
        await using var ctx = new RouteContext().AddRoutes(b =>
        {
            b.OnException<InvalidOperationException>().Handled();
            b.From("direct://h-in").RouteId("handled").ThrowException<InvalidOperationException>("h");
        });
        await ctx.Start();
        var completed = ctx.Notify().FromRoute("handled").WhenCompleted(1).Create();

        await ctx.SendBody("direct://h-in", "a");

        (await completed.MatchesAsync(Wait)).Should().BeTrue();
        completed.FailedCount.Should().Be(0);
    }

    [Fact]
    public async Task FromRoute_ScopesToOneRoute()
    {
        await using var ctx = new RouteContext().AddRoutes(b =>
        {
            b.From("direct://a").RouteId("a").To("mock://a");
            b.From("direct://b").RouteId("b").To("mock://b");
        });
        await ctx.Start();
        var onlyA = ctx.Notify().FromRoute("a").WhenDone(1).Create();

        await ctx.SendBody("direct://b", "x");
        (await onlyA.MatchesAsync(Short)).Should().BeFalse();

        await ctx.SendBody("direct://a", "x");
        (await onlyA.MatchesAsync(Wait)).Should().BeTrue();
    }

    [Fact]
    public async Task From_UriMask_ScopesByConsumerEndpoint()
    {
        await using var ctx = new RouteContext().AddRoutes(b =>
        {
            b.From("direct://orders-in").RouteId("o").To("mock://o");
            b.From("direct://other").RouteId("x").To("mock://x");
        });
        await ctx.Start();
        var orders = ctx.Notify().From("direct://orders-*").WhenReceived(1).Create();

        await ctx.SendBody("direct://other", "1");
        (await orders.MatchesAsync(Short)).Should().BeFalse();

        await ctx.SendBody("direct://orders-in", "1");
        (await orders.MatchesAsync(Wait)).Should().BeTrue();
    }

    [Fact]
    public async Task Filter_Condition_CountsOnlyMatchingExchanges()
    {
        await using var ctx = new RouteContext().AddRoutes(b => b.From("direct://fl-in").RouteId("fl").To("mock://fl"));
        await ctx.Start();
        var vip = ctx.Notify().FromRoute("fl").Filter("header.kind == 'vip'").WhenDone(1).Create();

        await ctx.SendBodyAndHeader("direct://fl-in", "1", "kind", "std");
        (await vip.MatchesAsync(Short)).Should().BeFalse();

        await ctx.SendBodyAndHeader("direct://fl-in", "2", "kind", "vip");
        (await vip.MatchesAsync(Wait)).Should().BeTrue();
    }

    [Fact]
    public async Task Reset_AndDispose()
    {
        await using var ctx = new RouteContext().AddRoutes(b => b.From("direct://r-in").RouteId("r").To("mock://r"));
        await ctx.Start();
        var matcher = ctx.Notify().FromRoute("r").WhenDone(1).Create();

        await ctx.SendBody("direct://r-in", "1");
        matcher.Matches().Should().BeTrue();

        matcher.Reset();
        matcher.Matches().Should().BeFalse();

        matcher.Dispose();
        await ctx.SendBody("direct://r-in", "2");
        matcher.DoneCount.Should().Be(0, "a disposed matcher ignores events");
    }

    [Fact]
    public async Task Create_WithoutCondition_IsRejected()
    {
        await using var ctx = new RouteContext();

        var act = () => ctx.Notify().FromRoute("r").Create();

        act.Should().Throw<InvalidOperationException>().WithMessage("*at least one When*");
    }
}

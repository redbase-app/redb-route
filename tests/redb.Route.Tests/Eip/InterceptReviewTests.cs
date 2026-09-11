using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.TestKit;

namespace redb.Route.Tests.Eip;

/// <summary>
/// Defects from the 2026-09-01 code review (docs/V4/REVIEW-CODE-2026-09-01.md, В4 and the blank-mask
/// item): the target URI an interception publishes in headers is sanitized, and a blank From mask is
/// refused where it is declared instead of silently dropping the route at compile.
/// </summary>
public class InterceptReviewTests
{
    [Fact]
    public async Task InterceptSendToEndpoint_PublishesTheTargetWithoutThePassword()
    {
        await using var ctx = new RouteContext().AddRoutes(b => b
            .From("direct://secret")
                .InterceptSendToEndpoint("amqp://*").To("mock://tap").SkipSendToOriginalEndpoint().EndIntercept()
                .To("amqp://user:s3cret@broker/orders"));
        await ctx.Start();

        await ctx.SendBody("direct://secret", "x");

        var headers = ctx.Mock("mock://tap").ReceivedExchanges[0].In.Headers;
        headers["redb.toEndpoint"].Should().Be(EndpointUri.Sanitize("amqp://user:s3cret@broker/orders"));
        headers["CamelToEndpoint"]!.ToString().Should().NotContain("s3cret", "a message header travels through replies, logs and the dashboard");
    }

    [Fact]
    public async Task InterceptFrom_BlankMask_IsRefusedAtDeclaration()
    {
        await using var ctx = new RouteContext();

        var act = async () =>
        {
            ctx.AddRoutes(b =>
            {
                b.InterceptFrom("   ").SetHeader("x", 1);
                b.From("direct://blank").To("mock://blank");
            });
            await ctx.Start();
        };

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*InterceptFrom*");
    }

    // Second batch of the review (intercept order, {{config}} targets, dynamic targets, regex masks).

    [Fact]
    public async Task Intercepts_RunInDeclarationOrder_BuilderLevelFirst()
    {
        var order = new List<string>();
        await using var ctx = new RouteContext().AddRoutes(b =>
        {
            b.Intercept().Process(_ => order.Add("builder"));
            b.From("direct://order")
                .Intercept().Process(_ => order.Add("A")).EndIntercept()
                .Intercept().Process(_ => order.Add("B")).EndIntercept()
                .SetHeader("x", 1);
        });
        await ctx.Start();

        await ctx.SendBody("direct://order", "m");

        order.Should().Equal("builder", "A", "B");
    }

    [Fact]
    public async Task InterceptSendToEndpoint_MatchesAConfigPlaceholderTarget_ByItsResolvedUri()
    {
        await using var ctx = new RouteContext();
        ctx.SetProperty("audit.endpoint", "kafka://audit");   // container-free {{key}} source
        ctx.AddRoutes(b => b
            .From("direct://cfg")
                .InterceptSendToEndpoint("kafka://*").To("mock://cfg-tap").SkipSendToOriginalEndpoint().EndIntercept()
                .To("{{audit.endpoint}}"));
        await ctx.Start();

        await ctx.SendBody("direct://cfg", "m");   // unmatched, the real kafka send would fail: no such component here

        await ctx.Mock("mock://cfg-tap").ExpectMessageCount(1).ExpectHeader("redb.toEndpoint", "kafka://audit").AssertIsSatisfiedAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task InterceptSendToEndpoint_EvaluatesADynamicTargetOnce()
    {
        var evaluations = 0;
        await using var ctx = new RouteContext().AddRoutes(b => b
            .From("direct://once")
                .InterceptSendToEndpoint("mock://*").SetHeader("tapped", true).EndIntercept()
                .ToD(_ => { Interlocked.Increment(ref evaluations); return "mock://dyn-once"; }));
        await ctx.Start();

        await ctx.SendBody("direct://once", "m");

        evaluations.Should().Be(1, "the wrapper resolves the target and the send uses that very URI");
        await ctx.Mock("mock://dyn-once").ExpectMessageCount(1).ExpectHeader("tapped", true).AssertIsSatisfiedAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task InterceptSendToEndpoint_InvalidRegexMask_IsRefusedAtDeclaration()
    {
        await using var ctx = new RouteContext();

        var act = async () =>
        {
            ctx.AddRoutes(b => b.From("direct://rx").InterceptSendToEndpoint("regex:[").To("mock://x").EndIntercept().To("mock://y"));
            await ctx.Start();
        };

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*regular expression*");
    }
}

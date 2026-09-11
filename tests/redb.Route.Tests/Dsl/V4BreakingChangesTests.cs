using System.Diagnostics;
using redb.Route.Core;
using redb.Route.TestKit;

namespace redb.Route.Tests.Dsl;

/// <summary>The 4.0 breaking bundle (docs/V4/09-BREAKING.md): string verbs that replaced the *Expression(string) synonyms, and the consumer-URI guard.</summary>
public class V4BreakingChangesTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(2);

    [Fact]
    public async Task Loop_String_CountsFromTheMessage()
    {
        var iterations = 0;
        await using var ctx = new RouteContext().AddRoutes(b => b
            .From("direct://loop").Loop("${header.count}").Process(_ => Interlocked.Increment(ref iterations)).EndLoop());
        await ctx.Start();

        await ctx.SendBodyAndHeader("direct://loop", "x", "count", 3);
        await ctx.SendBodyAndHeader("direct://loop", "x", "count", 2);

        iterations.Should().Be(5);
    }

    [Fact]
    public async Task Loop_String_BareExpression_Works()
    {
        var iterations = 0;
        await using var ctx = new RouteContext().AddRoutes(b => b
            .From("direct://loop2").Loop("header.count * 2").Process(_ => Interlocked.Increment(ref iterations)).EndLoop());
        await ctx.Start();

        await ctx.SendBodyAndHeader("direct://loop2", "x", "count", 2);

        iterations.Should().Be(4);
    }

    [Fact]
    public async Task Delay_String_MillisecondsFromTheMessage()
    {
        await using var ctx = new RouteContext().AddRoutes(b => b.From("direct://delay").Delay("${header.ms}").To("mock://delayed"));
        await ctx.Start();

        var clock = Stopwatch.StartNew();
        await ctx.SendBodyAndHeader("direct://delay", "x", "ms", 150);

        clock.Elapsed.Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(120));
        await ctx.Mock("mock://delayed").ExpectMessageCount(1).AssertIsSatisfiedAsync(Wait);
    }

    [Fact]
    public async Task Throttle_String_LimitFromTheMessage()
    {
        await using var ctx = new RouteContext().AddRoutes(b => b
            .From("direct://thr").Throttle("${header.rate}", TimeSpan.FromSeconds(10)).RejectOnOverflow().To("mock://thr").EndThrottle());
        await ctx.Start();

        for (var i = 0; i < 5; i++)
            await ctx.SendBodyAndHeader("direct://thr", i, "rate", 2);

        await ctx.Mock("mock://thr").ExpectMessageCount(2).AssertIsSatisfiedAsync(Wait);
    }

    [Fact]
    public async Task Throttle_String_MalformedExpression_FailsAtStart()
    {
        await using var ctx = new RouteContext().AddRoutes(b => b.From("direct://thr-bad").Throttle("max(header.rate, ", TimeSpan.FromSeconds(1)).EndThrottle());

        var act = () => ctx.Start();

        await act.Should().ThrowAsync<Exception>();
    }

    [Fact]
    public async Task DollarPlaceholder_InConsumerUri_FailsAtStart()
    {
        await using var ctx = new RouteContext().AddRoutes(b => b.From("direct://in-${header.tenant}").To("mock://x"));

        var act = () => ctx.Start();

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*consumer URI*${...}*no message*");
    }
}

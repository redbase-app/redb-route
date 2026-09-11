using redb.Route.Core;
using redb.Route.TestKit;

namespace redb.Route.Tests.TestKit;

/// <summary>
/// Code review 2026-09-01: a builder added after an advice is still built at Start(), and an advice
/// reaches the steps inside OnCompletion and intercept bodies.
/// </summary>
public class AdviceWithReviewTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(2);

    [Fact]
    public async Task ABuilderAddedAfterAnAdvice_IsStillBuilt()
    {
        await using var ctx = new RouteContext().AddRoutes(b => b.From("direct://a").To("kafka://a-out"));
        ctx.AdviceAllRoutes(a => a.MockEndpoints("kafka://*"));
        ctx.AddRoutes(b => b.From("direct://b").To("mock://b-out"));
        await ctx.Start();

        await ctx.SendBody("direct://b", "late");

        await ctx.Mock("mock://b-out").ExpectBodies("late").AssertIsSatisfiedAsync(Wait);
    }

    [Fact]
    public async Task MockEndpoints_ReachesAnOnCompletionBody()
    {
        await using var ctx = new RouteContext().AddRoutes(b => b
            .From("direct://c").OnCompletion().To("kafka://audit").EndOnCompletion().SetBody("done"));
        ctx.AdviceAllRoutes(a => a.MockEndpoints("kafka://*"));
        await ctx.Start();

        await ctx.SendBody("direct://c", "x");

        await ctx.Mock("kafka://audit").ExpectMessageCount(1).AssertIsSatisfiedAsync(Wait);
    }
}

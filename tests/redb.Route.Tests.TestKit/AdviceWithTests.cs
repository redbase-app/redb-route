using redb.Route.Abstractions;
using redb.Route.Components;
using redb.Route.Core;
using redb.Route.Definitions;
using redb.Route.TestKit;

namespace redb.Route.Tests.TestKit;

/// <summary>
/// AdviceWith: the route under test is written against real endpoints (kafka, sql) whose components
/// are not even registered in this test process — exactly the "no broker" situation the kit is for.
/// </summary>
public class AdviceWithTests
{
    private sealed class OrdersRoutes : RouteBuilder
    {
        protected override void Configure()
        {
            From("kafka://orders").RouteId("orders")
                .Choice()
                    .When((IExchange e) => e.In.Headers.TryGetValue("priority", out var p) && Equals(p, "high")).To("kafka://orders-vip")
                    .Otherwise().To("kafka://orders-std")
                .EndChoice()
                .To("sql:INSERT INTO audit").Id("audit");
        }
    }

    [Fact]
    public async Task WithoutAdvice_RouteOnUnregisteredScheme_DoesNotStart()
    {
        await using var ctx = new RouteContext().AddRoutes(new OrdersRoutes());

        var act = () => ctx.Start();

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*kafka*");
    }

    [Fact]
    public async Task ReplaceFrom_And_MockEndpoints_RunTheRouteWithoutBrokers()
    {
        await using var ctx = new RouteContext().AddRoutes(new OrdersRoutes());
        ctx.AdviceRoute("orders", a => a
            .ReplaceFrom("direct://test-in")
            .MockEndpoints("kafka://*", "sql:*"));
        await ctx.Start();

        var vip = ctx.Mock("kafka://orders-vip").ExpectMessageCount(1).ExpectHeader("priority", "high");
        var std = ctx.Mock("kafka://orders-std").ExpectMessageCount(0);
        var audit = ctx.Mock("sql:INSERT INTO audit").ExpectMessageCount(1);

        await ctx.SendBodyAndHeader("direct://test-in", "order-1", "priority", "high");

        await vip.AssertIsSatisfiedAsync(TimeSpan.FromSeconds(2));
        await std.AssertIsSatisfiedAsync(TimeSpan.FromSeconds(1));
        await audit.AssertIsSatisfiedAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task MockEndpoints_ReachesTo_InsideSplit()
    {
        await using var ctx = new RouteContext().AddRoutes(b => b
            .From("direct://split-in").RouteId("split")
                .Split(e => (IEnumerable<object?>)e.In.Body!).To("kafka://items").EndSplit());
        ctx.AdviceRoute("split", a => a.MockEndpoints("kafka://*"));
        await ctx.Start();

        await ctx.SendBody("direct://split-in", new object?[] { "a", "b", "c" });

        await ctx.Mock("kafka://items").ExpectBodies("a", "b", "c").AssertIsSatisfiedAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task MockEndpoints_ReachesTo_InsideMulticast()
    {
        await using var ctx = new RouteContext().AddRoutes(b => b
            .From("direct://mc-in").RouteId("mc")
                .Multicast().To("kafka://a").To("kafka://b").EndMulticast());
        ctx.AdviceRoute("mc", a => a.MockEndpoints("kafka://*"));
        await ctx.Start();

        await ctx.SendBody("direct://mc-in", "x");

        await ctx.Mock("kafka://a").ExpectBodies("x").AssertIsSatisfiedAsync(TimeSpan.FromSeconds(2));
        await ctx.Mock("kafka://b").ExpectBodies("x").AssertIsSatisfiedAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task MockEndpoints_ReachesTo_InsideCatchBranch()
    {
        await using var ctx = new RouteContext().AddRoutes(b => b
            .From("direct://tc-in").RouteId("tc")
                .TryCatch()
                    .ThrowException<InvalidOperationException>("boom")
                .Catch<InvalidOperationException>()
                    .To("kafka://errors")
                .EndTryCatch());
        ctx.AdviceRoute("tc", a => a.MockEndpoints("kafka://*"));
        await ctx.Start();

        await ctx.SendBody("direct://tc-in", "x");

        await ctx.Mock("kafka://errors").ExpectMessageCount(1).AssertIsSatisfiedAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task MockEndpoints_ReachesTo_InsideCircuitBreakerFallback()
    {
        await using var ctx = new RouteContext().AddRoutes(b => b
            .From("direct://cb-in").RouteId("cb")
                .CircuitBreaker()
                    .ThrowException<InvalidOperationException>("down")
                    .FallBack(f => f.To("kafka://fallback"))
                .EndCircuitBreaker());
        ctx.AdviceRoute("cb", a => a.MockEndpoints("kafka://*"));
        await ctx.Start();

        // Structural proof: the fallback branch is reached through IBranchingDefinition, not per-type code.
        var breaker = ctx.Routes.Single().Definition.Outputs.OfType<IBranchingDefinition>().Single();
        var fallbackTo = breaker.Branches.Single().Outputs.OfType<ToDefinition>().Single();
        fallbackTo.Uri.Should().Be("mock://kafka:fallback");
    }

    [Fact]
    public async Task WeaveById_Replace_Before_After()
    {
        await using var ctx = new RouteContext().AddRoutes(b => b
            .From("direct://w-in").RouteId("w")
                .SetBody("one").Id("step-a")
                .To("mock://out"));
        ctx.AdviceRoute("w", a => a
            .WeaveById("step-a").Before(r => r.SetHeader("before", "yes"))
            .WeaveById("step-a").After(r => r.SetHeader("after", "yes"))
            .WeaveById("step-a").Replace(r => r.SetBody("two")));
        await ctx.Start();

        await ctx.SendBody("direct://w-in", "input");

        await ctx.Mock("mock://out")
            .ExpectBodies("two")
            .ExpectHeader("before", "yes")
            .ExpectHeader("after", "yes")
            .AssertIsSatisfiedAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task WeaveById_Remove_DropsTheStep()
    {
        await using var ctx = new RouteContext().AddRoutes(b => b
            .From("direct://rm-in").RouteId("rm")
                .SetBody("replaced").Id("setter")
                .To("mock://rm-out"));
        ctx.AdviceRoute("rm", a => a.WeaveById("setter").Remove());
        await ctx.Start();

        await ctx.SendBody("direct://rm-in", "input");

        await ctx.Mock("mock://rm-out").ExpectBodies("input").AssertIsSatisfiedAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task WeaveByToUri_And_WeaveByType_SelectSteps()
    {
        await using var ctx = new RouteContext().AddRoutes(b => b
            .From("direct://wt-in").RouteId("wt")
                .SetHeader("h", "1")
                .To("kafka://x"));
        ctx.AdviceRoute("wt", a => a
            .WeaveByToUri("kafka://*").Replace(r => r.To("mock://replaced"))
            .WeaveByType<SetHeaderStaticDefinition>().Remove());
        await ctx.Start();

        await ctx.SendBody("direct://wt-in", "body");

        var mock = ctx.Mock("mock://replaced").ExpectMessageCount(1);
        await mock.AssertIsSatisfiedAsync(TimeSpan.FromSeconds(2));
        mock.ReceivedExchanges[0].In.Headers.Should().NotContainKey("h");
    }

    [Fact]
    public async Task WeaveAddFirst_And_WeaveAddLast()
    {
        await using var ctx = new RouteContext().AddRoutes(b => b
            .From("direct://add-in").RouteId("add")
                .To("mock://middle"));
        ctx.AdviceRoute("add", a => a
            .WeaveAddFirst(r => r.SetHeader("first", true))
            .WeaveAddLast(r => r.To("mock://last")));
        await ctx.Start();

        await ctx.SendBody("direct://add-in", "x");

        await ctx.Mock("mock://middle").ExpectHeader("first", true).AssertIsSatisfiedAsync(TimeSpan.FromSeconds(2));
        await ctx.Mock("mock://last").ExpectMessageCount(1).AssertIsSatisfiedAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task AdviceAllRoutes_AppliesToEveryRoute()
    {
        await using var ctx = new RouteContext().AddRoutes(b =>
        {
            b.From("direct://all-1").RouteId("r1").To("kafka://one");
            b.From("direct://all-2").RouteId("r2").To("kafka://two");
        });
        ctx.AdviceAllRoutes(a => a.MockEndpoints("kafka://*"));
        await ctx.Start();

        await ctx.SendBody("direct://all-1", "1");
        await ctx.SendBody("direct://all-2", "2");

        await ctx.Mock("kafka://one").ExpectBodies("1").AssertIsSatisfiedAsync(TimeSpan.FromSeconds(2));
        await ctx.Mock("kafka://two").ExpectBodies("2").AssertIsSatisfiedAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task UnnamedRoute_IsAdvisedByItsEndpointDerivedId()
    {
        await using var ctx = new RouteContext().AddRoutes(b => b
            .From("direct://unnamed").To("kafka://u"));
        // A route with no RouteId() is named after its endpoint, and that name is computable:
        // the same call the context makes when it compiles the route.
        var derivedId = RouteIdFactory.ForEndpoint(EndpointUriParser.Parse("direct://unnamed"));
        derivedId.Should().StartWith("direct-unnamed-");
        ctx.AdviceRoute(derivedId, a => a.MockEndpoints("kafka://*"));
        await ctx.Start();

        await ctx.SendBody("direct://unnamed", "x");

        await ctx.Mock("kafka://u").ExpectMessageCount(1).AssertIsSatisfiedAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task UnknownRouteId_FailsWithKnownIds()
    {
        await using var ctx = new RouteContext().AddRoutes(new OrdersRoutes());

        var act = () => ctx.AdviceRoute("nope", a => a.MockEndpoints("kafka://*"));

        act.Should().Throw<InvalidOperationException>().WithMessage("*no route with id 'nope'*Known routes: 'orders'*");
    }

    [Fact]
    public async Task WeaveThatMatchesNothing_Fails()
    {
        await using var ctx = new RouteContext().AddRoutes(new OrdersRoutes());

        var act = () => ctx.AdviceRoute("orders", a => a.WeaveById("missing").Remove());

        act.Should().Throw<InvalidOperationException>().WithMessage("*no step matches id 'missing'*");
    }

    [Fact]
    public async Task AdviceAfterStart_IsRejected()
    {
        await using var ctx = new RouteContext().AddRoutes(b => b.From("direct://late").RouteId("late").To("mock://late"));
        await ctx.Start();

        var act = () => ctx.AdviceRoute("late", a => a.MockEndpoints("mock://*"));

        act.Should().Throw<InvalidOperationException>().WithMessage("*before RouteContext.Start()*");
    }

    [Fact]
    public async Task MockedTo_IsRegisteredAsMockEndpoint_WithCamelStyleName()
    {
        await using var ctx = new RouteContext().AddRoutes(b => b
            .From("direct://name-in").RouteId("name").To("kafka://orders-vip?acks=all"));
        ctx.AdviceRoute("name", a => a.MockEndpoints("kafka://*"));
        await ctx.Start();

        await ctx.SendBody("direct://name-in", "x");

        var byOriginal = ctx.Mock("kafka://orders-vip?acks=all");
        var byMockName = ctx.Mock("mock://kafka:orders-vip");
        byOriginal.Should().BeSameAs(byMockName);
        byOriginal.Should().BeOfType<MockEndpoint>();
        byOriginal.ReceivedCount.Should().Be(1);
    }
}

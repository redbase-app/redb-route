using redb.Route.Core;
using redb.Route.TestKit;

namespace redb.Route.Tests.Eip;

/// <summary>Intercept(), InterceptFrom(), InterceptSendToEndpoint() — route-level and builder-level.</summary>
public class InterceptTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(2);

    [Fact]
    public async Task Intercept_RunsBeforeEveryStep_IncludingStepsNestedInScopes()
    {
        var hits = 0;
        await using var ctx = new RouteContext().AddRoutes(b => b
            .From("direct://every")
                .Intercept().Process(_ => Interlocked.Increment(ref hits)).EndIntercept()
                .SetHeader("a", 1)
                .Split(e => (IEnumerable<object?>)e.In.Body!).To("mock://parts").EndSplit()
                .To("mock://end"));
        await ctx.Start();

        await ctx.SendBody("direct://every", new object?[] { "x", "y" });

        // SetHeader, Split, To inside the split (twice: one per fragment), To at the end.
        hits.Should().Be(5);
        await ctx.Mock("mock://parts").ExpectMessageCount(2).AssertIsSatisfiedAsync(Wait);
    }

    [Fact]
    public async Task Intercept_When_GatesTheInterceptSteps()
    {
        await using var ctx = new RouteContext().AddRoutes(b => b
            .From("direct://when")
                .Intercept().When("header.audit").To("mock://audit").EndIntercept()
                .SetHeader("x", 1)
                .To("mock://out"));
        await ctx.Start();

        await ctx.SendBodyAndHeader("direct://when", "a", "audit", true);
        await ctx.Mock("mock://audit").ExpectMessageCount(2).AssertIsSatisfiedAsync(Wait);   // one per step

        await ctx.SendBodyAndHeader("direct://when", "b", "audit", false);
        await ctx.Mock("mock://audit").ExpectMessageCount(2).AssertIsSatisfiedAsync(Wait);   // unchanged
        await ctx.Mock("mock://out").ExpectMessageCount(2).AssertIsSatisfiedAsync(Wait);
    }

    [Fact]
    public async Task Intercept_Stop_StopsTheExchange()
    {
        await using var ctx = new RouteContext().AddRoutes(b => b
            .From("direct://stop")
                .Intercept().When("header.block").Stop().EndIntercept()
                .To("mock://out-stop"));
        await ctx.Start();

        await ctx.SendBodyAndHeader("direct://stop", "a", "block", true);
        await ctx.SendBodyAndHeader("direct://stop", "b", "block", false);

        await ctx.Mock("mock://out-stop").ExpectBodies("b").AssertIsSatisfiedAsync(Wait);
    }

    [Fact]
    public async Task BuilderLevel_Intercept_AppliesToEveryRoute()
    {
        await using var ctx = new RouteContext().AddRoutes(b =>
        {
            b.Intercept().SetHeader("seen", true);
            b.From("direct://r1").To("mock://r1");
            b.From("direct://r2").To("mock://r2");
        });
        await ctx.Start();

        await ctx.SendBody("direct://r1", "1");
        await ctx.SendBody("direct://r2", "2");

        await ctx.Mock("mock://r1").ExpectHeader("seen", true).AssertIsSatisfiedAsync(Wait);
        await ctx.Mock("mock://r2").ExpectHeader("seen", true).AssertIsSatisfiedAsync(Wait);
    }

    [Fact]
    public async Task InterceptFrom_RunsOncePerMessage_OnlyForMatchingConsumers()
    {
        var entries = 0;
        await using var ctx = new RouteContext().AddRoutes(b =>
        {
            b.InterceptFrom("direct://orders-*").Process(_ => Interlocked.Increment(ref entries)).SetHeader("entry", true);
            b.From("direct://orders-in").SetHeader("a", 1).SetHeader("b", 2).To("mock://orders");
            b.From("direct://other").To("mock://other");
        });
        await ctx.Start();

        await ctx.SendBody("direct://orders-in", "o");
        await ctx.SendBody("direct://other", "x");

        entries.Should().Be(1, "InterceptFrom fires on entry, not per step");
        await ctx.Mock("mock://orders").ExpectHeader("entry", true).AssertIsSatisfiedAsync(Wait);
        ctx.Mock("mock://other").ReceivedExchanges[0].In.Headers.Should().NotContainKey("entry");
    }

    [Fact]
    public async Task InterceptSendToEndpoint_Skip_IsADryRun_TheOriginalEndpointIsNeverResolved()
    {
        // kafka:// has no component in this process: a real send would fail. With the intercept the
        // send is replaced, so the route runs — the plan's "dry run in production" case.
        await using var ctx = new RouteContext().AddRoutes(b => b
            .From("direct://dry")
                .InterceptSendToEndpoint("kafka://*").When("property.dryRun")
                    .To("mock://dry-tap")
                    .SkipSendToOriginalEndpoint()
                .EndIntercept()
                .SetProperty("dryRun", e => e.In.Headers.ContainsKey("dry"))
                .To("kafka://orders")
                .To("mock://after"));
        await ctx.Start();

        await ctx.SendBodyAndHeader("direct://dry", "o1", "dry", true);
        await ctx.Mock("mock://dry-tap")
            .ExpectBodies("o1")
            .ExpectHeader("redb.toEndpoint", "kafka://orders")
            .ExpectHeader("CamelToEndpoint", "kafka://orders")
            .AssertIsSatisfiedAsync(Wait);
        await ctx.Mock("mock://after").ExpectMessageCount(1).AssertIsSatisfiedAsync(Wait);

        var real = () => ctx.SendBody("direct://dry", "o2");
        await real.Should().ThrowAsync<InvalidOperationException>().WithMessage("*kafka*", "without dryRun the original send runs and there is no kafka component");
    }

    [Fact]
    public async Task InterceptSendToEndpoint_WithoutSkip_TapsAndSends()
    {
        await using var ctx = new RouteContext().AddRoutes(b => b
            .From("direct://tap")
                .InterceptSendToEndpoint("mock://orig").To("mock://tap").EndIntercept()
                .To("mock://orig")
                .To("mock://untouched"));
        await ctx.Start();

        await ctx.SendBody("direct://tap", "m");

        await ctx.Mock("mock://tap").ExpectBodies("m").AssertIsSatisfiedAsync(Wait);
        await ctx.Mock("mock://orig").ExpectBodies("m").AssertIsSatisfiedAsync(Wait);
        ctx.Mock("mock://untouched").ReceivedExchanges[0].In.Headers.Should().ContainKey("redb.toEndpoint", "the header stays on the exchange after the intercepted send");
    }

    [Fact]
    public async Task InterceptSendToEndpoint_MatchesDynamicTargets_PerMessage()
    {
        await using var ctx = new RouteContext().AddRoutes(b => b
            .From("direct://dyn")
                .InterceptSendToEndpoint("mock://dyn-*").SetHeader("tapped", true).EndIntercept()
                .ToD("mock://${header.target}"));
        await ctx.Start();

        await ctx.SendBodyAndHeader("direct://dyn", "a", "target", "dyn-1");
        await ctx.SendBodyAndHeader("direct://dyn", "b", "target", "plain");

        await ctx.Mock("mock://dyn-1").ExpectHeader("tapped", true).ExpectHeader("CamelToEndpoint", "mock://dyn-1").AssertIsSatisfiedAsync(Wait);
        ctx.Mock("mock://plain").ReceivedExchanges[0].In.Headers.Should().NotContainKey("tapped");
    }

    [Fact]
    public async Task InterceptBody_IsNotInterceptedItself()
    {
        var hits = 0;
        await using var ctx = new RouteContext().AddRoutes(b => b
            .From("direct://self")
                .Intercept().Process(_ => Interlocked.Increment(ref hits)).SetHeader("i", 1).EndIntercept()
                .To("mock://self-out"));
        await ctx.Start();

        await ctx.SendBody("direct://self", "x");

        hits.Should().Be(1, "the intercept's own two steps must not be wrapped by the intercept");
    }

    [Fact]
    public void SkipSendToOriginalEndpoint_OnPlainIntercept_IsRejected()
    {
        var act = () => new RouteBuilderProbe().Probe();

        act.Should().Throw<InvalidOperationException>().WithMessage("*InterceptSendToEndpoint*");
    }

    private sealed class RouteBuilderProbe : RouteBuilder
    {
        public void Probe() => Intercept().SkipSendToOriginalEndpoint();
        protected override void Configure() { }
    }
}

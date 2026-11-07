using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Processors;
using redb.Route.TestKit;

namespace redb.Route.Tests.Processors;

/// <summary>Keyed throttle with the limit read from the message: key and limit both come from the exchange.</summary>
public class KeyedThrottleDynamicLimitTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(10);

    private static async Task SendBurst(IRouteContext ctx, string uri, int goldCount, int stdCount)
    {
        for (var i = 0; i < goldCount; i++) await ctx.SendBodyAndHeader(uri, $"g{i}", "tier", "gold");
        for (var i = 0; i < stdCount; i++) await ctx.SendBodyAndHeader(uri, $"s{i}", "tier", "std");
    }

    [Fact]
    public async Task DynamicLimit_PerKey_GoldGetsFive_StdGetsTwo_InOneWindow()
    {
        await using var ctx = new RouteContext().AddRoutes(b => b
            .From("direct://kt")
                .Throttle(e => (string)e.In.Headers["tier"]!, e => Equals(e.In.Headers["tier"], "gold") ? 5 : 2, Window)
                    .RejectOnOverflow()
                    .To("mock://kt-out")
                .EndKeyedThrottle());
        await ctx.Start();

        await SendBurst(ctx, "direct://kt", goldCount: 6, stdCount: 6);

        var mock = ctx.Mock("mock://kt-out");
        await mock.ExpectMessageCount(7).AssertIsSatisfiedAsync(Wait);
        mock.ReceivedExchanges.Count(e => Equals(e.In.Headers["tier"], "gold")).Should().Be(5);
        mock.ReceivedExchanges.Count(e => Equals(e.In.Headers["tier"], "std")).Should().Be(2);
    }

    [Fact]
    public async Task FixedLimit_CannotExpressPerKeyLimits_BothKeysGetTwo()
    {
        // The pre-existing int overload: same limit for every key — the asymmetry the dynamic form removes.
        await using var ctx = new RouteContext().AddRoutes(b => b
            .From("direct://kt-fixed")
                .Throttle(e => (string)e.In.Headers["tier"]!, 2, Window)
                    .RejectOnOverflow()
                    .To("mock://kt-fixed-out")
                .EndKeyedThrottle());
        await ctx.Start();

        await SendBurst(ctx, "direct://kt-fixed", goldCount: 6, stdCount: 6);

        await ctx.Mock("mock://kt-fixed-out").ExpectMessageCount(4).AssertIsSatisfiedAsync(Wait);
    }

    [Fact]
    public async Task StringForm_KeyAndLimitExpressions()
    {
        await using var ctx = new RouteContext().AddRoutes(b => b
            .From("direct://kt-expr")
                .Throttle("header.tier", "header.tier == 'gold' ? 5 : 2", Window)
                    .RejectOnOverflow()
                    .To("mock://kt-expr-out")
                .EndKeyedThrottle());
        await ctx.Start();

        await SendBurst(ctx, "direct://kt-expr", goldCount: 6, stdCount: 6);

        var mock = ctx.Mock("mock://kt-expr-out");
        await mock.ExpectMessageCount(7).AssertIsSatisfiedAsync(Wait);
        mock.ReceivedExchanges.Count(e => Equals(e.In.Headers["tier"], "gold")).Should().Be(5);
    }

    [Fact]
    public async Task StringForm_MalformedLimitExpression_FailsAtDslTime()
    {
        var act = () => new RouteContext().AddRoutes(b => b
            .From("direct://kt-bad").Throttle("header.tier", "max(header.a, ", Window).EndKeyedThrottle()).Start();

        await act.Should().ThrowAsync<Exception>();
    }

    [Fact]
    public async Task RejectedExchanges_Carry429_AndRetryAfter()
    {
        await using var ctx = new RouteContext().AddRoutes(b => b
            .From("direct://kt-429")
                .Throttle(e => "one", e => 1, Window).RejectOnOverflow().To("mock://kt-429-out").EndKeyedThrottle()
                .To("mock://kt-429-tail"));
        await ctx.Start();

        using var template = new ProducerTemplate(ctx);
        template.Start();
        var first = Exchange.Create(new Message("first"), null);
        var second = Exchange.Create(new Message("second"), null);
        await template.SendAsync("direct://kt-429", first);
        await template.SendAsync("direct://kt-429", second);

        ctx.Mock("mock://kt-429-out").ReceivedCount.Should().Be(1);
        ctx.Mock("mock://kt-429-tail").ReceivedCount.Should().Be(1, "a rejected exchange is stopped, it does not continue down the route");
        second.IsStopped.Should().BeTrue();
        // The pipeline hands Out over to In between steps, so the 429 reply is read from whichever holds it.
        var reply = second.Out ?? second.In;
        reply.Headers["redbHttp.ResponseCode"].Should().Be(429);
        reply.Headers["Retry-After"].Should().Be("10");
    }

    [Fact]
    public async Task WaitMode_DynamicLimit_EventuallyPassesEverything()
    {
        await using var ctx = new RouteContext().AddRoutes(b => b
            .From("direct://kt-wait")
                .Throttle(e => (string)e.In.Headers["tier"]!, e => 2, TimeSpan.FromMilliseconds(150))
                    .To("mock://kt-wait-out")
                .EndKeyedThrottle());
        await ctx.Start();

        var sends = Enumerable.Range(0, 5).Select(i => ctx.SendBodyAndHeader("direct://kt-wait", i, "tier", "std")).ToArray();
        await Task.WhenAll(sends);

        await ctx.Mock("mock://kt-wait-out").ExpectMessageCount(5).AssertIsSatisfiedAsync(Wait);
    }

    [Fact]
    public void Processor_EvictsIdleGates()
    {
        var next = new DelegateProcessor(_ => { });
        using var throttle = new KeyedThrottleProcessor(next, e => (string)e.In.Body!, _ => 1, TimeSpan.FromMilliseconds(20));

        throttle.Process(Exchange.Create(new Message("k1"), null)).GetAwaiter().GetResult();
        throttle.ActiveKeyCount.Should().Be(1);

        Thread.Sleep(120);   // > 2 × period and the slot is released after one period
        throttle.Process(Exchange.Create(new Message("k2"), null)).GetAwaiter().GetResult();

        throttle.ActiveKeyCount.Should().Be(1, "k1 was idle for two periods and got evicted when k2 arrived");
    }
}

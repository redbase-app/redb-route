using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Definitions;
using redb.Route.Processors.LoadBalancer;

namespace redb.Route.Tests.Processors;

/// <summary>
/// Unit tests for Load Balancer strategies and processor.
/// </summary>
public class LoadBalancerTests
{
    private static readonly string[] ThreeEndpoints = ["http://a:8080", "http://b:8080", "http://c:8080"];

    // ══════════════════════════════════════════════════════════════
    // RoundRobinStrategy
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public void RoundRobin_CyclesEvenly()
    {
        var strategy = new RoundRobinStrategy();
        var exchange = CreateExchange();

        var results = new List<string>();
        for (var i = 0; i < 9; i++)
            results.Add(strategy.Select(exchange, ThreeEndpoints));

        results.Should().BeEquivalentTo([
            "http://a:8080", "http://b:8080", "http://c:8080",
            "http://a:8080", "http://b:8080", "http://c:8080",
            "http://a:8080", "http://b:8080", "http://c:8080"
        ]);
    }

    [Fact]
    public void RoundRobin_SingleEndpoint_AlwaysReturnsSame()
    {
        var strategy = new RoundRobinStrategy();
        var exchange = CreateExchange();
        var single = new[] { "http://only:8080" };

        for (var i = 0; i < 5; i++)
            strategy.Select(exchange, single).Should().Be("http://only:8080");
    }

    [Fact]
    public void RoundRobin_EmptyEndpoints_Throws()
    {
        var strategy = new RoundRobinStrategy();
        var exchange = CreateExchange();

        var act = () => strategy.Select(exchange, Array.Empty<string>());
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void RoundRobin_ThreadSafety()
    {
        var strategy = new RoundRobinStrategy();
        var exchange = CreateExchange();
        var counts = new ConcurrentDictionary<string, int>();
        foreach (var ep in ThreeEndpoints) counts[ep] = 0;

        const int iterations = 3000;
        Parallel.For(0, iterations, _ =>
        {
            var selected = strategy.Select(exchange, ThreeEndpoints);
            counts.AddOrUpdate(selected, 1, (_, old) => old + 1);
        });

        // With round-robin, each endpoint should get exactly 1000 picks
        foreach (var ep in ThreeEndpoints)
            counts[ep].Should().Be(1000);
    }

    // ══════════════════════════════════════════════════════════════
    // RandomStrategy
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public void Random_DistributesAcrossEndpoints()
    {
        var strategy = new RandomStrategy();
        var exchange = CreateExchange();
        var hits = new HashSet<string>();

        // With 1000 iterations and 3 endpoints, extremely unlikely to miss any
        for (var i = 0; i < 1000; i++)
            hits.Add(strategy.Select(exchange, ThreeEndpoints));

        hits.Should().BeEquivalentTo(ThreeEndpoints);
    }

    [Fact]
    public void Random_SingleEndpoint_AlwaysReturnsSame()
    {
        var strategy = new RandomStrategy();
        var exchange = CreateExchange();
        var single = new[] { "http://only:8080" };

        for (var i = 0; i < 10; i++)
            strategy.Select(exchange, single).Should().Be("http://only:8080");
    }

    [Fact]
    public void Random_EmptyEndpoints_Throws()
    {
        var strategy = new RandomStrategy();
        var exchange = CreateExchange();

        var act = () => strategy.Select(exchange, Array.Empty<string>());
        act.Should().Throw<InvalidOperationException>();
    }

    // ══════════════════════════════════════════════════════════════
    // FailoverStrategy
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public void Failover_SelectsFirstHealthy()
    {
        var strategy = new FailoverStrategy(maxFailures: 2);
        var exchange = CreateExchange();

        strategy.Select(exchange, ThreeEndpoints).Should().Be("http://a:8080");
    }

    [Fact]
    public void Failover_SkipsFailedEndpoints()
    {
        var strategy = new FailoverStrategy(maxFailures: 2);
        var exchange = CreateExchange();

        // Fail endpoint A twice (reaches maxFailures)
        strategy.ReportFailure("http://a:8080", new Exception("down"));
        strategy.ReportFailure("http://a:8080", new Exception("down"));

        strategy.Select(exchange, ThreeEndpoints).Should().Be("http://b:8080");
    }

    [Fact]
    public void Failover_BelowMaxFailures_StillSelects()
    {
        var strategy = new FailoverStrategy(maxFailures: 3);
        var exchange = CreateExchange();

        // Only 1 failure, maxFailures is 3 — should still select A
        strategy.ReportFailure("http://a:8080", new Exception("flaky"));

        strategy.Select(exchange, ThreeEndpoints).Should().Be("http://a:8080");
    }

    [Fact]
    public void Failover_AllFailed_ReturnFirst()
    {
        var strategy = new FailoverStrategy(maxFailures: 1);
        var exchange = CreateExchange();

        foreach (var ep in ThreeEndpoints)
            strategy.ReportFailure(ep, new Exception("all down"));

        // Falls back to first endpoint
        strategy.Select(exchange, ThreeEndpoints).Should().Be("http://a:8080");
    }

    [Fact]
    public void Failover_RecoveryAfterTimeout()
    {
        // Use very short recovery timeout (already expired by definition)
        var strategy = new FailoverStrategy(maxFailures: 1, recoveryTimeout: TimeSpan.Zero);
        var exchange = CreateExchange();

        strategy.ReportFailure("http://a:8080", new Exception("down"));

        // Recovery timeout of zero + any elapsed time → should probe A again
        strategy.Select(exchange, ThreeEndpoints).Should().Be("http://a:8080");
    }

    [Fact]
    public void Failover_SuccessResetsFailureCount()
    {
        var strategy = new FailoverStrategy(maxFailures: 2);
        var exchange = CreateExchange();

        strategy.ReportFailure("http://a:8080", new Exception());
        strategy.ReportFailure("http://a:8080", new Exception());
        // A is now considered failed

        strategy.ReportSuccess("http://a:8080");
        // A should be healthy again
        strategy.Select(exchange, ThreeEndpoints).Should().Be("http://a:8080");
    }

    [Fact]
    public void Failover_EmptyEndpoints_Throws()
    {
        var strategy = new FailoverStrategy();
        var exchange = CreateExchange();

        var act = () => strategy.Select(exchange, Array.Empty<string>());
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Failover_InvalidMaxFailures_Throws()
    {
        var act = () => new FailoverStrategy(maxFailures: 0);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    // ══════════════════════════════════════════════════════════════
    // WeightedStrategy
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public void Weighted_RespectsWeightProportions()
    {
        var strategy = new WeightedStrategy([("http://heavy:8080", 3), ("http://light:8080", 1)]);
        var exchange = CreateExchange();
        var counts = new Dictionary<string, int> { ["http://heavy:8080"] = 0, ["http://light:8080"] = 0 };

        for (var i = 0; i < 400; i++)
            counts[strategy.Select(exchange, ["http://heavy:8080", "http://light:8080"])]++;

        // 3:1 ratio over 400 iterations = heavy: 300, light: 100
        counts["http://heavy:8080"].Should().Be(300);
        counts["http://light:8080"].Should().Be(100);
    }

    [Fact]
    public void Weighted_SingleWeight_AlwaysSame()
    {
        var strategy = new WeightedStrategy([("http://only:8080", 5)]);
        var exchange = CreateExchange();

        for (var i = 0; i < 10; i++)
            strategy.Select(exchange, ["http://only:8080"]).Should().Be("http://only:8080");
    }

    [Fact]
    public void Weighted_EmptyEndpoints_Throws()
    {
        var act = () => new WeightedStrategy([]);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Weighted_ZeroWeight_Throws()
    {
        var act = () => new WeightedStrategy([("http://a:8080", 0)]);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Weighted_NegativeWeight_Throws()
    {
        var act = () => new WeightedStrategy([("http://a:8080", -1)]);
        act.Should().Throw<ArgumentException>();
    }

    // ══════════════════════════════════════════════════════════════
    // StickyStrategy
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public void Sticky_SameKey_SameEndpoint()
    {
        var strategy = new StickyStrategy(e => e.In.GetHeader<string>("tenantId"));

        var exchange1 = CreateExchange(headers: new() { ["tenantId"] = "tenant-A" });
        var exchange2 = CreateExchange(headers: new() { ["tenantId"] = "tenant-A" });

        var ep1 = strategy.Select(exchange1, ThreeEndpoints);
        var ep2 = strategy.Select(exchange2, ThreeEndpoints);

        ep1.Should().Be(ep2);
    }

    [Fact]
    public void Sticky_DifferentKeys_CanSelectDifferentEndpoints()
    {
        var strategy = new StickyStrategy(e => e.In.GetHeader<string>("tenantId"));
        var hits = new HashSet<string>();

        // Generate enough keys to hit different endpoints
        for (var i = 0; i < 100; i++)
        {
            var exchange = CreateExchange(headers: new() { ["tenantId"] = $"tenant-{i}" });
            hits.Add(strategy.Select(exchange, ThreeEndpoints));
        }

        // Should hit at least 2 different endpoints (extremely likely with 100 keys and 3 endpoints)
        hits.Count.Should().BeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public void Sticky_NullKey_FallsBackToRoundRobin()
    {
        var strategy = new StickyStrategy(_ => null);
        var exchange = CreateExchange();

        // Should not throw — falls back to round-robin
        var selected = strategy.Select(exchange, ThreeEndpoints);
        selected.Should().BeOneOf(ThreeEndpoints);
    }

    [Fact]
    public void Sticky_EmptyKey_FallsBackToRoundRobin()
    {
        var strategy = new StickyStrategy(_ => "");
        var exchange = CreateExchange();

        var selected = strategy.Select(exchange, ThreeEndpoints);
        selected.Should().BeOneOf(ThreeEndpoints);
    }

    [Fact]
    public void Sticky_EmptyEndpoints_Throws()
    {
        var strategy = new StickyStrategy(_ => "key");
        var exchange = CreateExchange();

        var act = () => strategy.Select(exchange, Array.Empty<string>());
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Sticky_NullExtractor_Throws()
    {
        var act = () => new StickyStrategy(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Failover_NegativeRecoveryTimeout_Throws()
    {
        var act = () => new FailoverStrategy(maxFailures: 3, recoveryTimeout: TimeSpan.FromSeconds(-1));
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    // ══════════════════════════════════════════════════════════════
    // ILoadBalancerStrategy default methods
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public void DefaultInterface_ReportMethods_AreNoOp()
    {
        // RoundRobin doesn't override Report methods — verify default no-ops work
        ILoadBalancerStrategy strategy = new RoundRobinStrategy();

        // Should not throw
        strategy.ReportFailure("http://a:8080", new Exception("test"));
        strategy.ReportSuccess("http://a:8080");
    }

    // ══════════════════════════════════════════════════════════════
    // DSL integration
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public void DSL_LoadBalance_AddsStep()
    {
        var def = new RouteDefinition();
        def.From("direct://test");

        var strategy = new RoundRobinStrategy();
        def.LoadBalance(strategy, "http://a:8080", "http://b:8080");

        def.Steps.Should().ContainSingle(s => s is LoadBalanceStep);
        var step = def.Steps.OfType<LoadBalanceStep>().Single();
        step.Strategy.Should().BeSameAs(strategy);
        step.Endpoints.Should().BeEquivalentTo(["http://a:8080", "http://b:8080"]);
    }

    [Fact]
    public void DSL_LoadBalance_NullStrategy_Throws()
    {
        var def = new RouteDefinition();
        def.From("direct://test");

        var act = () => def.LoadBalance(null!, "http://a:8080");
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void DSL_LoadBalance_NoUris_Throws()
    {
        var def = new RouteDefinition();
        def.From("direct://test");

        var act = () => def.LoadBalance(new RoundRobinStrategy());
        act.Should().Throw<ArgumentException>();
    }

    // ══════════════════════════════════════════════════════════════
    // Helpers
    // ══════════════════════════════════════════════════════════════

    private static IExchange CreateExchange(object? body = null, Dictionary<string, object>? headers = null)
    {
        var msg = new Message { Body = body ?? "test" };
        if (headers != null)
        {
            foreach (var (k, v) in headers)
                msg.Headers[k] = v;
        }
        return new Exchange(msg);
    }
}

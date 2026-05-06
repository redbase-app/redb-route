using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using redb.Route.Abstractions;
using redb.Route.Components;
using redb.Route.Core;
using redb.Route.Processors;
using redb.Route.Processors.LoadBalancer;

namespace redb.Route.Tests.Processors;

/// <summary>
/// Integration tests for <see cref="LoadBalancerProcessor"/> using real DirectComponent endpoints.
/// Tests the full pipeline: strategy selection → ToProcessor → DirectEndpoint → DirectProducer → consumer.
/// </summary>
[Trait("Category", "Integration")]
public class LoadBalancerIntegrationTests : IAsyncDisposable
{
    private readonly RouteContext _context = new();
    private readonly ConcurrentBag<(string Endpoint, object? Body)> _received = [];
    private readonly List<IConsumer> _consumers = [];

    /// <summary>
    /// Registers a consumer on a direct endpoint that captures received exchanges.
    /// </summary>
    private async Task RegisterConsumer(string name)
    {
        var endpoint = (DirectEndpoint)_context.GetEndpoint($"direct:{name}");
        var processor = new DelegateProcessor((exchange, _) =>
        {
            _received.Add((name, exchange.In.Body));
            return Task.CompletedTask;
        });
        var consumer = endpoint.CreateConsumer(processor);
        await consumer.Start();
        _consumers.Add(consumer);
    }

    // ══════════════════════════════════════════════════════════════
    // RoundRobin — full pipeline
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public async Task RoundRobin_FullPipeline_DistributesEvenly()
    {
        var endpoints = new[] { "direct:lb-rr-a", "direct:lb-rr-b", "direct:lb-rr-c" };
        foreach (var ep in endpoints)
            await RegisterConsumer(ep.Replace("direct:", ""));

        var lb = new LoadBalancerProcessor(_context, endpoints, new RoundRobinStrategy());

        for (var i = 0; i < 9; i++)
            await lb.Process(new Exchange(new Message($"msg-{i}")));

        _received.Count.Should().Be(9);
        _received.Count(r => r.Endpoint == "lb-rr-a").Should().Be(3);
        _received.Count(r => r.Endpoint == "lb-rr-b").Should().Be(3);
        _received.Count(r => r.Endpoint == "lb-rr-c").Should().Be(3);
    }

    // ══════════════════════════════════════════════════════════════
    // Random — full pipeline
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Random_FullPipeline_AllEndpointsReceive()
    {
        var endpoints = new[] { "direct:lb-rand-a", "direct:lb-rand-b", "direct:lb-rand-c" };
        foreach (var ep in endpoints)
            await RegisterConsumer(ep.Replace("direct:", ""));

        var lb = new LoadBalancerProcessor(_context, endpoints, new RandomStrategy());

        for (var i = 0; i < 300; i++)
            await lb.Process(new Exchange(new Message($"msg-{i}")));

        _received.Count.Should().Be(300);
        // All three endpoints should have received at least some messages
        _received.Select(r => r.Endpoint).Distinct().Count().Should().Be(3);
    }

    // ══════════════════════════════════════════════════════════════
    // Failover — full pipeline with real failures
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Failover_FullPipeline_RetriesOnFailure()
    {
        // Register consumer on endpoint B only — A will throw (no consumer registered)
        var endpoints = new[] { "direct:lb-fo-a", "direct:lb-fo-b" };
        await RegisterConsumer("lb-fo-b");

        var strategy = new FailoverStrategy(maxFailures: 1);
        var lb = new LoadBalancerProcessor(_context, endpoints, strategy);

        // First exchange should fail on A, retry on B
        var exchange = new Exchange(new Message("failover-test"));
        await lb.Process(exchange);

        _received.Count.Should().Be(1);
        _received.Single().Endpoint.Should().Be("lb-fo-b");
    }

    [Fact]
    public async Task Failover_FullPipeline_AllFailed_Throws()
    {
        // No consumers registered — all endpoints will fail
        var endpoints = new[] { "direct:lb-fo-fail-a", "direct:lb-fo-fail-b" };

        var strategy = new FailoverStrategy(maxFailures: 1);
        var lb = new LoadBalancerProcessor(_context, endpoints, strategy);

        var exchange = new Exchange(new Message("all-fail"));
        var act = () => lb.Process(exchange);
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    // ══════════════════════════════════════════════════════════════
    // Sticky — full pipeline
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Sticky_FullPipeline_SameKeySameEndpoint()
    {
        var endpoints = new[] { "direct:lb-sticky-a", "direct:lb-sticky-b" };
        foreach (var ep in endpoints)
            await RegisterConsumer(ep.Replace("direct:", ""));

        var strategy = new StickyStrategy(e => e.In.GetHeader<string>("tenantId"));
        var lb = new LoadBalancerProcessor(_context, endpoints, strategy);

        // Send 10 exchanges with tenantId = "ABC"
        for (var i = 0; i < 10; i++)
        {
            var exchange = new Exchange(new Message($"sticky-{i}"));
            exchange.In.Headers["tenantId"] = "ABC";
            await lb.Process(exchange);
        }

        _received.Count.Should().Be(10);
        // All should go to the same endpoint
        _received.Select(r => r.Endpoint).Distinct().Count().Should().Be(1);
    }

    // ══════════════════════════════════════════════════════════════
    // Weighted — full pipeline
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Weighted_FullPipeline_RespectsWeights()
    {
        var endpoints = new[] { "direct:lb-wt-heavy", "direct:lb-wt-light" };
        foreach (var ep in endpoints)
            await RegisterConsumer(ep.Replace("direct:", ""));

        var strategy = new WeightedStrategy([("direct:lb-wt-heavy", 3), ("direct:lb-wt-light", 1)]);
        var lb = new LoadBalancerProcessor(_context, endpoints, strategy);

        for (var i = 0; i < 400; i++)
            await lb.Process(new Exchange(new Message($"weighted-{i}")));

        _received.Count.Should().Be(400);
        _received.Count(r => r.Endpoint == "lb-wt-heavy").Should().Be(300);
        _received.Count(r => r.Endpoint == "lb-wt-light").Should().Be(100);
    }

    // ══════════════════════════════════════════════════════════════
    // Concurrent access — thread safety under load
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public async Task RoundRobin_Concurrent_NoDataLoss()
    {
        var endpoints = new[] { "direct:lb-conc-a", "direct:lb-conc-b", "direct:lb-conc-c" };
        foreach (var ep in endpoints)
            await RegisterConsumer(ep.Replace("direct:", ""));

        var lb = new LoadBalancerProcessor(_context, endpoints, new RoundRobinStrategy());

        const int total = 300;
        var tasks = Enumerable.Range(0, total)
            .Select(i => lb.Process(new Exchange(new Message($"concurrent-{i}"))))
            .ToArray();
        await Task.WhenAll(tasks);

        _received.Count.Should().Be(total);
    }

    // ══════════════════════════════════════════════════════════════
    // Body passthrough — data integrity
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public async Task LoadBalancer_PreservesExchangeBody()
    {
        await RegisterConsumer("lb-body-a");
        var lb = new LoadBalancerProcessor(
            _context,
            ["direct:lb-body-a"],
            new RoundRobinStrategy());

        var payload = new { OrderId = 42, Product = "Widget" };
        await lb.Process(new Exchange(new Message(payload)));

        _received.Count.Should().Be(1);
        _received.Single().Body.Should().BeEquivalentTo(payload);
    }

    // ══════════════════════════════════════════════════════════════
    // Cleanup
    // ══════════════════════════════════════════════════════════════

    public async ValueTask DisposeAsync()
    {
        foreach (var consumer in _consumers)
            await consumer.Stop();
        await _context.DisposeAsync();
    }
}

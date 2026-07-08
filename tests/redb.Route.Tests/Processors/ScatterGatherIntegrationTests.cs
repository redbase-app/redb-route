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
using redb.Route.Definitions;
using redb.Route.Processors;

namespace redb.Route.Tests.Processors;

/// <summary>
/// Integration tests for <see cref="ScatterGatherProcessor"/> using real DirectComponent endpoints.
/// Tests the full pipeline: scatter (clone + send to each recipient) → gather (aggregate responses).
/// </summary>
[Trait("Category", "Integration")]
public class ScatterGatherIntegrationTests : IAsyncDisposable
{
    private readonly RouteContext _context = new();
    private readonly List<IConsumer> _consumers = [];

    /// <summary>
    /// Registers a consumer on a direct endpoint that processes exchanges via a delegate.
    /// </summary>
    private async Task RegisterConsumer(string name, Func<IExchange, CancellationToken, Task> handler)
    {
        var endpoint = (DirectEndpoint)_context.GetEndpoint($"direct:{name}");
        var processor = new DelegateProcessor(handler);
        var consumer = endpoint.CreateConsumer(processor);
        await consumer.Start();
        _consumers.Add(consumer);
    }

    /// <summary>
    /// Registers a simple consumer that sets a header on the exchange.
    /// </summary>
    private Task RegisterPriceConsumer(string name, decimal price)
    {
        return RegisterConsumer(name, (exchange, _) =>
        {
            exchange.In.Headers["price"] = price;
            exchange.In.Headers["supplier"] = name;
            return Task.CompletedTask;
        });
    }

    // ══════════════════════════════════════════════════════════════
    // Parallel — all succeed
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Parallel_AllSucceed_AggregatesBestPrice()
    {
        await RegisterPriceConsumer("sg-p-a", 100m);
        await RegisterPriceConsumer("sg-p-b", 50m);
        await RegisterPriceConsumer("sg-p-c", 75m);

        var sg = new ScatterGatherProcessor(
            _context,
            ["direct:sg-p-a", "direct:sg-p-b", "direct:sg-p-c"],
            (best, current) =>
            {
                var bestPrice = (decimal)best.In.Headers["price"];
                var currentPrice = (decimal)current.In.Headers["price"];
                return currentPrice < bestPrice ? current : best;
            });

        var exchange = new Exchange(new Message("get-quote"));
        await sg.Process(exchange);

        // Best price is 50 from supplier sg-p-b
        exchange.In.Headers["price"].Should().Be(50m);
        exchange.In.Headers["supplier"].Should().Be("sg-p-b");
    }

    // ══════════════════════════════════════════════════════════════
    // Sequential — all succeed
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Sequential_AllSucceed_AggregatesResults()
    {
        await RegisterPriceConsumer("sg-s-a", 200m);
        await RegisterPriceConsumer("sg-s-b", 150m);
        await RegisterPriceConsumer("sg-s-c", 300m);

        var sg = new ScatterGatherProcessor(
            _context,
            ["direct:sg-s-a", "direct:sg-s-b", "direct:sg-s-c"],
            (best, current) =>
            {
                var bestPrice = (decimal)best.In.Headers["price"];
                var currentPrice = (decimal)current.In.Headers["price"];
                return currentPrice < bestPrice ? current : best;
            },
            parallelProcessing: false);

        var exchange = new Exchange(new Message("get-quote"));
        await sg.Process(exchange);

        exchange.In.Headers["price"].Should().Be(150m);
        exchange.In.Headers["supplier"].Should().Be("sg-s-b");
    }

    // ══════════════════════════════════════════════════════════════
    // Dynamic recipients
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public async Task DynamicRecipients_ResolvedAtRuntime()
    {
        await RegisterPriceConsumer("sg-d-a", 10m);
        await RegisterPriceConsumer("sg-d-b", 20m);

        var sg = new ScatterGatherProcessor(
            _context,
            recipientFactory: e =>
                e.In.GetHeader<string>("targets")!.Split(','),
            aggregationStrategy: (best, current) =>
            {
                var bestPrice = (decimal)best.In.Headers["price"];
                var currentPrice = (decimal)current.In.Headers["price"];
                return currentPrice < bestPrice ? current : best;
            });

        var exchange = new Exchange(new Message("rfq"));
        exchange.In.Headers["targets"] = "direct:sg-d-a,direct:sg-d-b";
        await sg.Process(exchange);

        exchange.In.Headers["price"].Should().Be(10m);
    }

    // ══════════════════════════════════════════════════════════════
    // Timeout + stopOnException → TimeoutException
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Timeout_StopOnException_ThrowsTimeoutException()
    {
        await RegisterPriceConsumer("sg-t1-a", 100m);
        await RegisterConsumer("sg-t1-slow", async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
        });

        var sg = new ScatterGatherProcessor(
            _context,
            ["direct:sg-t1-a", "direct:sg-t1-slow"],
            (a, b) => a,
            stopOnException: true,
            timeout: TimeSpan.FromMilliseconds(200));

        var exchange = new Exchange(new Message("test"));
        var act = () => sg.Process(exchange);

        await act.Should().ThrowAsync<TimeoutException>();
    }

    // ══════════════════════════════════════════════════════════════
    // Timeout + best-effort → partial results
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Timeout_BestEffort_AggregatesPartialResults()
    {
        await RegisterPriceConsumer("sg-t2-fast", 42m);
        await RegisterConsumer("sg-t2-slow", async (exchange, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
            exchange.In.Headers["price"] = 999m;
        });

        var sg = new ScatterGatherProcessor(
            _context,
            ["direct:sg-t2-fast", "direct:sg-t2-slow"],
            (best, current) =>
            {
                var bestPrice = (decimal)best.In.Headers["price"];
                var currentPrice = (decimal)current.In.Headers["price"];
                return currentPrice < bestPrice ? current : best;
            },
            stopOnException: false,
            timeout: TimeSpan.FromMilliseconds(300));

        var exchange = new Exchange(new Message("test"));
        await sg.Process(exchange);

        // Only the fast endpoint's response is aggregated
        exchange.In.Headers["price"].Should().Be(42m);
    }

    // ══════════════════════════════════════════════════════════════
    // One endpoint fails — stopOnException=true → exception
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public async Task OneEndpointFails_StopOnException_Throws()
    {
        await RegisterPriceConsumer("sg-f1-a", 100m);
        await RegisterConsumer("sg-f1-err", (_, _) =>
            throw new InvalidOperationException("Supplier unavailable"));

        var sg = new ScatterGatherProcessor(
            _context,
            ["direct:sg-f1-a", "direct:sg-f1-err"],
            (a, b) => a,
            stopOnException: true);

        var exchange = new Exchange(new Message("test"));
        var act = () => sg.Process(exchange);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Supplier unavailable");
    }

    // ══════════════════════════════════════════════════════════════
    // One endpoint fails — best-effort → aggregates rest
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public async Task OneEndpointFails_BestEffort_AggregatesRest()
    {
        await RegisterPriceConsumer("sg-f2-a", 100m);
        await RegisterPriceConsumer("sg-f2-b", 80m);
        await RegisterConsumer("sg-f2-err", (_, _) =>
            throw new InvalidOperationException("Supplier down"));

        // Aggregation: pick cheapest from non-failed responses
        var sg = new ScatterGatherProcessor(
            _context,
            ["direct:sg-f2-a", "direct:sg-f2-err", "direct:sg-f2-b"],
            (best, current) =>
            {
                // Skip failed exchanges
                if (current.Exception != null) return best;
                if (best.Exception != null) return current;
                var bestPrice = (decimal)best.In.Headers["price"];
                var currentPrice = (decimal)current.In.Headers["price"];
                return currentPrice < bestPrice ? current : best;
            },
            stopOnException: false);

        var exchange = new Exchange(new Message("test"));
        await sg.Process(exchange);

        // Best non-failed price is 80 from sg-f2-b
        exchange.In.Headers["price"].Should().Be(80m);
    }

    // ══════════════════════════════════════════════════════════════
    // Sequential — timeout + best-effort → partial results
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Sequential_Timeout_BestEffort_AggregatesPartial()
    {
        await RegisterPriceConsumer("sg-st-fast1", 10m);
        await RegisterPriceConsumer("sg-st-fast2", 20m);
        await RegisterConsumer("sg-st-slow", async (exchange, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
            exchange.In.Headers["price"] = 5m;
        });

        var sg = new ScatterGatherProcessor(
            _context,
            ["direct:sg-st-fast1", "direct:sg-st-fast2", "direct:sg-st-slow"],
            (best, current) =>
            {
                var bestPrice = (decimal)best.In.Headers["price"];
                var currentPrice = (decimal)current.In.Headers["price"];
                return currentPrice < bestPrice ? current : best;
            },
            parallelProcessing: false,
            stopOnException: false,
            timeout: TimeSpan.FromMilliseconds(300));

        var exchange = new Exchange(new Message("test"));
        await sg.Process(exchange);

        // fast1 (10) and fast2 (20) succeeded before timeout; slow didn't finish
        exchange.In.Headers["price"].Should().Be(10m);
    }

    // ══════════════════════════════════════════════════════════════
    // Concurrent scatter-gathers — no data loss
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Concurrent_NoInterference()
    {
        // Each scatter-gather invocation accumulates bodies into a list
        await RegisterConsumer("sg-c-a", (exchange, _) =>
        {
            exchange.In.Body = $"A:{exchange.In.Body}";
            return Task.CompletedTask;
        });
        await RegisterConsumer("sg-c-b", (exchange, _) =>
        {
            exchange.In.Body = $"B:{exchange.In.Body}";
            return Task.CompletedTask;
        });

        var sg = new ScatterGatherProcessor(
            _context,
            ["direct:sg-c-a", "direct:sg-c-b"],
            (acc, cur) =>
            {
                acc.In.Body = $"{acc.In.Body}|{cur.In.Body}";
                return acc;
            });

        var results = new ConcurrentBag<string>();
        var tasks = Enumerable.Range(0, 50).Select(async i =>
        {
            var exchange = new Exchange(new Message($"msg-{i}"));
            await sg.Process(exchange);
            results.Add((string)exchange.In.Body!);
        });

        await Task.WhenAll(tasks);

        results.Should().HaveCount(50);
        // Each result should contain both A: and B: prefixes
        foreach (var result in results)
        {
            result.Should().Contain("A:");
            result.Should().Contain("B:");
        }
    }

    // ══════════════════════════════════════════════════════════════
    // Preserves exchange properties after aggregation
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public async Task PreservesPropertiesAfterAggregation()
    {
        await RegisterConsumer("sg-prop-a", (exchange, _) =>
        {
            exchange.Properties["source"] = "A";
            exchange.In.Headers["result"] = "from-A";
            return Task.CompletedTask;
        });

        var sg = new ScatterGatherProcessor(
            _context,
            ["direct:sg-prop-a"],
            (acc, cur) => cur);

        var exchange = new Exchange(new Message("test"));
        await sg.Process(exchange);

        exchange.In.Headers["result"].Should().Be("from-A");
        exchange.Properties["source"].Should().Be("A");
    }

    // ══════════════════════════════════════════════════════════════
    // MaxDOP=1 forces sequential-like execution
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public async Task MaxDop1_LimitsParallelism()
    {
        var concurrency = 0;
        var maxConcurrency = 0;
        var lockObj = new object();

        await RegisterConsumer("sg-dop-a", async (exchange, ct) =>
        {
            var c = Interlocked.Increment(ref concurrency);
            lock (lockObj) { if (c > maxConcurrency) maxConcurrency = c; }
            await Task.Delay(50, ct);
            Interlocked.Decrement(ref concurrency);
            exchange.In.Headers["done"] = true;
        });
        await RegisterConsumer("sg-dop-b", async (exchange, ct) =>
        {
            var c = Interlocked.Increment(ref concurrency);
            lock (lockObj) { if (c > maxConcurrency) maxConcurrency = c; }
            await Task.Delay(50, ct);
            Interlocked.Decrement(ref concurrency);
            exchange.In.Headers["done"] = true;
        });
        await RegisterConsumer("sg-dop-c", async (exchange, ct) =>
        {
            var c = Interlocked.Increment(ref concurrency);
            lock (lockObj) { if (c > maxConcurrency) maxConcurrency = c; }
            await Task.Delay(50, ct);
            Interlocked.Decrement(ref concurrency);
            exchange.In.Headers["done"] = true;
        });

        var sg = new ScatterGatherProcessor(
            _context,
            ["direct:sg-dop-a", "direct:sg-dop-b", "direct:sg-dop-c"],
            (acc, cur) => cur,
            parallelProcessing: true,
            maxDegreeOfParallelism: 1);

        var exchange = new Exchange(new Message("test"));
        await sg.Process(exchange);

        // With DOP=1, max concurrency should be 1
        maxConcurrency.Should().Be(1);
    }

    // ══════════════════════════════════════════════════════════════
    // Builder DSL — full integration
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Builder_FullConfig_IntegrationTest()
    {
        await RegisterPriceConsumer("sg-bld-a", 500m);
        await RegisterPriceConsumer("sg-bld-b", 250m);

        var def = new RouteDefinition();
        def.From("direct://sg-builder-test");
        def.ScatterGather(sg => sg
            .Recipients("direct:sg-bld-a", "direct:sg-bld-b")
            .AggregationStrategy((best, current) =>
            {
                var bestPrice = (decimal)best.In.Headers["price"];
                var currentPrice = (decimal)current.In.Headers["price"];
                return currentPrice < bestPrice ? current : best;
            })
            .ParallelProcessing());

        // Verify definition was created correctly
        var sgDef = def.Outputs.OfType<ScatterGatherDefinition>().Single();
        sgDef.StaticRecipients.Should().HaveCount(2);
        sgDef.ParallelProcessing.Should().BeTrue();
    }

    // ══════════════════════════════════════════════════════════════
    // Dynamic recipients — empty list throws
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public async Task DynamicRecipients_EmptyList_Throws()
    {
        var sg = new ScatterGatherProcessor(
            _context,
            recipientFactory: _ => [],
            aggregationStrategy: (a, b) => a);

        var exchange = new Exchange(new Message("test"));
        var act = () => sg.Process(exchange);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*at least one recipient*");
    }

    // ══════════════════════════════════════════════════════════════
    // Caller cancellation — always propagated
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public async Task CallerCancellation_PropagatesImmediately()
    {
        await RegisterConsumer("sg-cancel", async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
        });

        var sg = new ScatterGatherProcessor(
            _context,
            ["direct:sg-cancel"],
            (a, b) => a);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var exchange = new Exchange(new Message("test"));
        var act = () => sg.Process(exchange, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // ══════════════════════════════════════════════════════════════
    // Dispose
    // ══════════════════════════════════════════════════════════════

    public async ValueTask DisposeAsync()
    {
        foreach (var consumer in _consumers)
            if (consumer is IAsyncDisposable d)
                await d.DisposeAsync();
        await _context.DisposeAsync();
    }
}

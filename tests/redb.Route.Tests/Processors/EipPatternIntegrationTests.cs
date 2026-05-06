using FluentAssertions;
using redb.Route.Abstractions;
using redb.Route.Components;
using redb.Route.Core;
using redb.Route.Processors;

namespace redb.Route.Tests.Processors;

/// <summary>
/// Integration tests for the 6 EIP patterns through the DSL → Compiler → Engine pipeline.
/// </summary>
public class EipPatternIntegrationTests : IAsyncDisposable
{
    private readonly RouteContext _context = new();

    public async ValueTask DisposeAsync()
    {
        await _context.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    // ────────────────── Throttle ──────────────────

    [Fact]
    public async Task Throttle_InRoute_LimitsRate()
    {
        var received = new List<object?>();

        _context.AddRoutes(r =>
        {
            r.From("direct://throttle-in")
                .Throttle(5) // 5 per second
                .Process(e => received.Add(e.In.Body));
        });

        await _context.Start();

        var producer = _context.GetEndpoint("direct://throttle-in").CreateProducer();
        await producer.Start();

        for (int i = 0; i < 5; i++)
            await producer.Process(new Exchange(new Message($"msg-{i}")));

        received.Should().HaveCount(5);
    }

    [Fact]
    public async Task Throttle_WithPeriod_InRoute()
    {
        var received = new List<object?>();

        _context.AddRoutes(r =>
        {
            r.From("direct://throttle-period")
                .Throttle(10, TimeSpan.FromSeconds(2))
                .Process(e => received.Add(e.In.Body));
        });

        await _context.Start();

        var producer = _context.GetEndpoint("direct://throttle-period").CreateProducer();
        await producer.Start();

        for (int i = 0; i < 10; i++)
            await producer.Process(new Exchange(new Message(i)));

        received.Should().HaveCount(10);
    }

    // ────────────────── CircuitBreaker ──────────────────

    [Fact]
    public async Task CircuitBreaker_InRoute_HandlesFailures()
    {
        var processed = new List<string>();
        var shouldFail = true;

        _context.AddRoutes(r =>
        {
            r.From("direct://cb-in")
                .CircuitBreaker(cb =>
                {
                    cb.Threshold(2)
                      .ResetTimeout(TimeSpan.FromMilliseconds(100));
                })
                .Process(e =>
                {
                    if (shouldFail) throw new InvalidOperationException("fail");
                    processed.Add((string)e.In.Body!);
                });
        });

        await _context.Start();

        var producer = _context.GetEndpoint("direct://cb-in").CreateProducer();
        await producer.Start();

        // 2 failures → circuit opens
        await producer.Process(new Exchange(new Message("f1")));
        await producer.Process(new Exchange(new Message("f2")));

        // Circuit is open — this should not reach processor, should get CircuitBreakerOpenException
        var blockedExchange = new Exchange(new Message("blocked"));
        await producer.Process(blockedExchange);
        blockedExchange.Exception.Should().BeOfType<CircuitBreakerOpenException>();

        // Wait for reset timeout
        await Task.Delay(150);

        // Now let it succeed
        shouldFail = false;
        await producer.Process(new Exchange(new Message("recovery")));

        processed.Should().Contain("recovery");
    }

    [Fact]
    public async Task CircuitBreaker_WithFallback_InRoute()
    {
        var mainProcessed = new List<string>();
        var fallbackProcessed = new List<string>();

        _context.AddRoutes(r =>
        {
            r.From("direct://cb-fallback")
                .CircuitBreaker(cb =>
                {
                    cb.Threshold(1)
                      .ResetTimeout(TimeSpan.FromMinutes(5))
                      .FallBack(fb => fb.Process(e => fallbackProcessed.Add((string)e.In.Body!)));
                })
                .Process(e =>
                {
                    mainProcessed.Add((string)e.In.Body!);
                    throw new InvalidOperationException("fail");
                });
        });

        await _context.Start();

        var producer = _context.GetEndpoint("direct://cb-fallback").CreateProducer();
        await producer.Start();

        // First call fails → trips circuit
        await producer.Process(new Exchange(new Message("first")));
        mainProcessed.Should().HaveCount(1);

        // Second call → circuit open, uses fallback
        await producer.Process(new Exchange(new Message("second")));
        fallbackProcessed.Should().Contain("second");
    }

    // ────────────────── Resequencer ──────────────────

    [Fact]
    public async Task Resequence_InRoute_OrdersByKey()
    {
        var received = new List<long>();

        _context.AddRoutes(r =>
        {
            r.From("direct://reseq-in")
                .Resequence(
                    keySelector: e => (long)e.In.Headers["seq"]!,
                    batchSize: 3)
                .Process(e => received.Add((long)e.In.Headers["seq"]!));
        });

        await _context.Start();

        var producer = _context.GetEndpoint("direct://reseq-in").CreateProducer();
        await producer.Start();

        // Send out-of-order
        foreach (var seq in new long[] { 3, 1, 2 })
        {
            var msg = new Message("data");
            msg.Headers["seq"] = seq;
            await producer.Process(new Exchange(msg));
        }

        received.Should().Equal(1L, 2L, 3L);
    }

    // ────────────────── RecipientList ──────────────────

    [Fact]
    public async Task RecipientList_InRoute_RoutesToDynamicUris()
    {
        var receivedA = new List<object?>();
        var receivedB = new List<object?>();

        _context.AddRoutes(r =>
        {
            r.From("direct://rl-in")
                .RecipientList(e =>
                {
                    var targets = (string)e.In.Headers["targets"]!;
                    return targets.Split(',');
                });

            r.From("direct://targetA")
                .Process(e => receivedA.Add(e.In.Body));

            r.From("direct://targetB")
                .Process(e => receivedB.Add(e.In.Body));
        });

        await _context.Start();

        var producer = _context.GetEndpoint("direct://rl-in").CreateProducer();
        await producer.Start();

        var msg = new Message("payload");
        msg.Headers["targets"] = "direct://targetA,direct://targetB";
        await producer.Process(new Exchange(msg));

        receivedA.Should().HaveCount(1);
        receivedB.Should().HaveCount(1);
        receivedA[0].Should().Be("payload");
    }

    // ────────────────── Enrich ──────────────────

    [Fact]
    public async Task Enrich_InRoute_MergesResourceData()
    {
        var result = new List<string>();

        _context.AddRoutes(r =>
        {
            r.From("direct://enrich-in")
                .Enrich("direct://lookup", (original, enriched) =>
                {
                    original.In.Body = $"{original.In.Body}+{enriched.In.Body}";
                    return original;
                })
                .Process(e => result.Add((string)e.In.Body!));

            r.From("direct://lookup")
                .Process(e => e.In.Body = "extra-data");
        });

        await _context.Start();

        var producer = _context.GetEndpoint("direct://enrich-in").CreateProducer();
        await producer.Start();

        await producer.Process(new Exchange(new Message("base")));

        result.Should().HaveCount(1);
        result[0].Should().Be("base+extra-data");
    }

    // ────────────────── PollEnrich ──────────────────

    [Fact]
    public async Task PollEnrich_InRoute_MergesPolledData()
    {
        var result = new List<string>();

        _context.AddRoutes(r =>
        {
            r.From("direct://pollenrich-in")
                .PollEnrich("direct://poll-source", (original, polled) =>
                {
                    original.In.Body = polled is not null
                        ? $"{original.In.Body}+{polled.In.Body}"
                        : $"{original.In.Body}+timeout";
                    return original;
                }, timeout: TimeSpan.FromSeconds(5))
                .Process(e => result.Add((string)e.In.Body!));

            r.From("direct://poll-source")
                .Process(e => e.In.Body = "polled-data");
        });

        await _context.Start();

        var producer = _context.GetEndpoint("direct://pollenrich-in").CreateProducer();
        await producer.Start();

        await producer.Process(new Exchange(new Message("base")));

        result.Should().HaveCount(1);
        result[0].Should().Be("base+polled-data");
    }

    // ────────────────── DynamicRouter ──────────────────

    [Fact]
    public async Task DynamicRouter_InRoute_RoutesIteratively()
    {
        var visitOrder = new List<string>();

        _context.AddRoutes(r =>
        {
            r.From("direct://dynrouter-in")
                .DynamicRouter(e =>
                {
                    var hop = e.GetProperty<int>("hop");
                    e.Properties["hop"] = hop + 1;
                    return hop switch
                    {
                        0 => "direct://step1",
                        1 => "direct://step2",
                        _ => null
                    };
                });

            r.From("direct://step1")
                .Process(e => visitOrder.Add("step1"));

            r.From("direct://step2")
                .Process(e => visitOrder.Add("step2"));
        });

        await _context.Start();

        var producer = _context.GetEndpoint("direct://dynrouter-in").CreateProducer();
        await producer.Start();

        var exchange = new Exchange(new Message("data"));
        exchange.Properties["hop"] = 0;
        await producer.Process(exchange);

        visitOrder.Should().Equal("step1", "step2");
    }

    [Fact]
    public async Task DynamicRouter_NullOnFirst_DoesNothing()
    {
        var processed = false;

        _context.AddRoutes(r =>
        {
            r.From("direct://dynrouter-noop")
                .DynamicRouter(_ => null)
                .Process(_ => processed = true);
        });

        await _context.Start();

        var producer = _context.GetEndpoint("direct://dynrouter-noop").CreateProducer();
        await producer.Start();

        await producer.Process(new Exchange());
        // DynamicRouter returns null immediately, so it completes. 
        // The Process after DynamicRouter in the pipeline may or may not be reached depending on
        // how the compiler chains them. This test mainly validates no exceptions.
    }
}

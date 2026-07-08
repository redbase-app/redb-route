using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using FluentAssertions;
using NSubstitute;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Processors;
using redb.Route.Telemetry;
using Xunit;

namespace redb.Route.Tests.Telemetry;

/// <summary>
/// Tests for processor-level metrics (ProcessorMetrics).
/// Verifies that each processor increments the correct counters.
/// </summary>
[Collection("Telemetry")]
public class ProcessorMetricsTests : IDisposable
{
    private readonly MeterListener _listener;
    private readonly ConcurrentBag<(string Name, long Value)> _measurements = [];

    public ProcessorMetricsTests()
    {
        _listener = new MeterListener();
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == RouteMetrics.MeterName)
                listener.EnableMeasurementEvents(instrument);
        };
        _listener.SetMeasurementEventCallback<long>((instrument, value, _, _) =>
        {
            _measurements.Add((instrument.Name, value));
        });
        _listener.Start();
    }

    public void Dispose()
    {
        _listener.Dispose();
    }

    private long Sum(string instrumentName) =>
        _measurements.Where(m => m.Name == instrumentName).Sum(m => m.Value);

    // ═══════════════════════════════════════════════════════════════════
    //  Circuit Breaker metrics
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task CircuitBreaker_Tripped_IncrementsMetric()
    {
        var failingNext = Substitute.For<IProcessor>();
        failingNext.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("fail")));

        var cb = new CircuitBreakerProcessor(failingNext, failureThreshold: 2);

        // Trigger 2 failures to trip the breaker
        for (int i = 0; i < 2; i++)
        {
            var ex = new Exchange(new Message("test"));
            await cb.Process(ex);
        }

        _listener.RecordObservableInstruments();
        Sum("redb.route.circuitbreaker.tripped").Should().BeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task CircuitBreaker_Rejected_IncrementsMetric()
    {
        var failingNext = Substitute.For<IProcessor>();
        failingNext.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("fail")));

        var cb = new CircuitBreakerProcessor(failingNext, failureThreshold: 1,
            resetTimeout: TimeSpan.FromHours(1));

        // Trip the circuit
        var ex1 = new Exchange(new Message("test"));
        await cb.Process(ex1);

        // Next call should be rejected
        var ex2 = new Exchange(new Message("test"));
        await cb.Process(ex2);

        _listener.RecordObservableInstruments();
        Sum("redb.route.circuitbreaker.rejected").Should().BeGreaterThanOrEqualTo(1);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Filter metrics
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Filter_DroppedExchange_IncrementsMetric()
    {
        var next = Substitute.For<IProcessor>();
        var filter = new FilterProcessor(_ => false, next);

        await filter.Process(new Exchange(new Message("test")));

        _listener.RecordObservableInstruments();
        Sum("redb.route.filter.dropped").Should().BeGreaterThanOrEqualTo(1);
        await next.DidNotReceive().Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Filter_PassedExchange_DoesNotIncrementDropped()
    {
        var next = Substitute.For<IProcessor>();
        var filter = new FilterProcessor(_ => true, next);
        var before = Sum("redb.route.filter.dropped");

        await filter.Process(new Exchange(new Message("test")));

        _listener.RecordObservableInstruments();
        Sum("redb.route.filter.dropped").Should().Be(before);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Splitter metrics
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Splitter_RecordsPartCount()
    {
        var target = Substitute.For<IProcessor>();
        var splitter = new SplitterProcessor(
            ex => new object[] { "a", "b", "c" },
            target);
        var before = Sum("redb.route.splitter.parts");

        await splitter.Process(new Exchange(new Message("test")));

        _listener.RecordObservableInstruments();
        Sum("redb.route.splitter.parts").Should().BeGreaterThanOrEqualTo(before + 3);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Debounce metrics
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Debounce_Discarded_IncrementsMetric()
    {
        var next = Substitute.For<IProcessor>();
        var db = new DebounceProcessor(next, _ => "key1", TimeSpan.FromHours(1));
        var before = Sum("redb.route.debounce.discarded");

        // First message — creates entry, no discard
        await db.Process(new Exchange(new Message("first")));
        // Second message — replaces first, should discard
        await db.Process(new Exchange(new Message("second")));

        _listener.RecordObservableInstruments();
        Sum("redb.route.debounce.discarded").Should().BeGreaterThanOrEqualTo(before + 1);

        db.Dispose();
    }

    [Fact]
    public async Task Debounce_Flushed_IncrementsMetric()
    {
        var next = Substitute.For<IProcessor>();
        var db = new DebounceProcessor(next, _ => "key1", TimeSpan.FromMilliseconds(50));

        await db.Process(new Exchange(new Message("test")));

        // Wait well beyond quiet period for the async flush to complete
        await Task.Delay(500);

        _listener.RecordObservableInstruments();
        // Global counter: just verify _next was actually called (the flush happened)
        await next.Received(1).Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>());
        // And the metric was incremented at least once (shared counter across tests)
        Sum("redb.route.debounce.flushed").Should().BeGreaterThanOrEqualTo(1);

        db.Dispose();
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Timeout metrics
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Timeout_Expired_IncrementsMetric()
    {
        var slowNext = Substitute.For<IProcessor>();
        slowNext.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(async ci =>
            {
                var ct = ci.ArgAt<CancellationToken>(1);
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            });

        var tp = new TimeoutProcessor(slowNext, TimeSpan.FromMilliseconds(100), "test-route");
        var before = Sum("redb.route.timeout.expired");
        var exchange = new Exchange(new Message("test"));

        await Assert.ThrowsAsync<ExchangeTimedOutException>(() => tp.Process(exchange, CancellationToken.None));

        _listener.RecordObservableInstruments();
        Sum("redb.route.timeout.expired").Should().BeGreaterThanOrEqualTo(before + 1);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Throttle metrics
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Throttle_Delayed_IncrementsWhenSlotsExhausted()
    {
        var next = Substitute.For<IProcessor>();
        // Slow processing to occupy the single slot
        next.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(Task.Delay(200));
        var throttle = new ThrottleProcessor(next, maxPerPeriod: 1, period: TimeSpan.FromSeconds(10));
        var before = Sum("redb.route.throttle.delayed");

        // First call takes the slot; second will be delayed
        var t1 = throttle.Process(new Exchange(new Message("1")));
        await Task.Delay(10); // let t1 acquire the semaphore
        var t2 = Task.Run(() => throttle.Process(new Exchange(new Message("2"))));
        await Task.Delay(10); // let t2 check CurrentCount == 0

        _listener.RecordObservableInstruments();
        Sum("redb.route.throttle.delayed").Should().BeGreaterThanOrEqualTo(before + 1);

        await t1;
        throttle.Dispose();
        // t2 may throw ObjectDisposedException — that's expected
    }
}

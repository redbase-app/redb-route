using redb.Route.Abstractions;
using redb.Route.Configuration;
using redb.Route.Core;

namespace redb.Route.Tests.Core;

/// <summary>
/// Integration tests covering graceful shutdown scenarios across Phases 2-5.
/// Uses direct:// endpoints for full pipeline testing without external infrastructure.
/// </summary>
public sealed class GracefulShutdownIntegrationTests
{
    // ═══════════════════════════════════════════════════════════════════
    // Phase 2: InflightDrainGuard — drain under load
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Drain_InflightExchangesComplete_BeforeStopReturns()
    {
        // Arrange — route that takes 200ms to process each message
        var processedIds = new List<string>();
        var processingStarted = new TaskCompletionSource();

        var ctx = new RouteContext(options: new RouteEngineOptions
        {
            EnableTelemetry = false,
            EnableMetrics = false,
            ShutdownTimeout = TimeSpan.FromSeconds(10)
        });

        ctx.AddRoutes(r =>
        {
            r.From("direct://drain-test")
             .RouteId("drain-route")
             .Process(async (e, ct) =>
             {
                 processingStarted.TrySetResult();
                 await Task.Delay(200, ct);
                 lock (processedIds) processedIds.Add(e.ExchangeId);
             });
        });

        await ctx.Start();

        var producer = ctx.GetEndpoint("direct://drain-test").CreateProducer();
        await producer.Start();

        // Act — send message, then immediately stop while it's still processing
        var exchange = new Exchange(new Message("inflight-msg"));
        var processTask = producer.Process(exchange, CancellationToken.None);

        // Wait for processing to actually start
        await processingStarted.Task;

        // Stop should wait for the inflight exchange to complete
        await ctx.Stop();
        await processTask;

        // Assert — the exchange must have completed processing before stop returned
        processedIds.Should().Contain(exchange.ExchangeId);
        ctx.InflightRepository.Browse().Should().BeEmpty("all inflight should be drained");

        await ctx.DisposeAsync();
    }

    [Fact]
    public async Task Drain_MultipleInflightExchanges_AllCompleteBeforeStop()
    {
        var processedCount = 0;
        var allStarted = new CountdownEvent(3);

        var ctx = new RouteContext(options: new RouteEngineOptions
        {
            EnableTelemetry = false,
            EnableMetrics = false,
            ShutdownTimeout = TimeSpan.FromSeconds(10)
        });

        ctx.AddRoutes(r =>
        {
            r.From("direct://drain-multi")
             .RouteId("drain-multi")
             .Process(async (e, ct) =>
             {
                 allStarted.Signal();
                 await Task.Delay(300, ct);
                 Interlocked.Increment(ref processedCount);
             });
        });

        await ctx.Start();

        var producer = ctx.GetEndpoint("direct://drain-multi").CreateProducer();
        await producer.Start();

        // Act — send 3 messages concurrently
        var tasks = Enumerable.Range(0, 3).Select(_ =>
            producer.Process(new Exchange(new Message("msg")), CancellationToken.None)).ToArray();

        // Wait for all to start processing
        allStarted.Wait(TimeSpan.FromSeconds(5));

        // Stop while all 3 are in flight
        await ctx.Stop();
        await Task.WhenAll(tasks);

        // Assert
        processedCount.Should().Be(3, "all inflight exchanges should complete before stop");
        ctx.InflightRepository.Browse().Should().BeEmpty();

        await ctx.DisposeAsync();
    }

    [Fact]
    public async Task Drain_InflightRepository_TracksExchangesDuringProcessing()
    {
        var processingReached = new TaskCompletionSource();
        var canFinish = new TaskCompletionSource();

        var ctx = new RouteContext(options: new RouteEngineOptions
        {
            EnableTelemetry = false,
            EnableMetrics = false
        });

        ctx.AddRoutes(r =>
        {
            r.From("direct://inflight-track")
             .RouteId("inflight-track")
             .Process(async (e, ct) =>
             {
                 processingReached.TrySetResult();
                 await canFinish.Task;
             });
        });

        await ctx.Start();

        var producer = ctx.GetEndpoint("direct://inflight-track").CreateProducer();
        await producer.Start();

        // Act — send message, wait for processing to start
        var processTask = producer.Process(new Exchange(new Message("tracked")), CancellationToken.None);
        await processingReached.Task;

        // Assert — inflight repo shows 1 exchange while processing
        ctx.InflightRepository.Browse("inflight-track").Should().HaveCount(1);

        // Let it finish
        canFinish.SetResult();
        await processTask;

        // After completion — inflight is empty
        ctx.InflightRepository.Browse("inflight-track").Should().BeEmpty();

        await ctx.Stop();
        await ctx.DisposeAsync();
    }

    // ═══════════════════════════════════════════════════════════════════
    // Phase 3: ShutdownTimeout — enforcement under real pipeline
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ShutdownTimeout_ForcesStop_WhenProcessingExceedsTimeout()
    {
        var ctx = new RouteContext(options: new RouteEngineOptions
        {
            EnableTelemetry = false,
            EnableMetrics = false,
            ShutdownTimeout = TimeSpan.FromMilliseconds(300)
        });

        ctx.AddRoutes(r =>
        {
            r.From("direct://slow-shutdown")
             .RouteId("slow-shutdown")
             .Process(async (e, ct) =>
             {
                 // Very slow processing — should be force-cancelled by ShutdownTimeout
                 await Task.Delay(TimeSpan.FromMinutes(5), ct);
             });
        });

        await ctx.Start();

        var producer = ctx.GetEndpoint("direct://slow-shutdown").CreateProducer();
        await producer.Start();

        // Start slow processing
        _ = producer.Process(new Exchange(new Message("slow")), CancellationToken.None);
        await Task.Delay(50); // let processing start

        // Act — Stop with timeout
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await ctx.Stop();
        sw.Stop();

        // Assert — should complete roughly within ShutdownTimeout, not 5 minutes
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5),
            "shutdown should be forced after ShutdownTimeout, not wait for slow processing");

        await ctx.DisposeAsync();
    }

    [Fact]
    public async Task ShutdownTimeout_AllowsCompletion_WhenProcessingFinishesInTime()
    {
        var completed = false;

        var ctx = new RouteContext(options: new RouteEngineOptions
        {
            EnableTelemetry = false,
            EnableMetrics = false,
            ShutdownTimeout = TimeSpan.FromSeconds(5)
        });

        ctx.AddRoutes(r =>
        {
            r.From("direct://fast-shutdown")
             .RouteId("fast-shutdown")
             .Process(async (e, ct) =>
             {
                 await Task.Delay(50, ct);
                 completed = true;
             });
        });

        await ctx.Start();

        var producer = ctx.GetEndpoint("direct://fast-shutdown").CreateProducer();
        await producer.Start();
        await producer.Process(new Exchange(new Message("fast")), CancellationToken.None);

        await ctx.Stop();

        // Assert — processing completed gracefully
        completed.Should().BeTrue("processing finished within ShutdownTimeout");

        await ctx.DisposeAsync();
    }

    // ═══════════════════════════════════════════════════════════════════
    // Phase 5: ProcessingTimeout — per-exchange timeout integration
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ProcessingTimeout_FiresOnExchangeTimedOut_ThroughFullPipeline()
    {
        var timedOutExchangeIds = new List<string>();
        var listener = new TimeoutTrackingListener(timedOutExchangeIds);

        var ctx = new RouteContext(options: new RouteEngineOptions
        {
            EnableTelemetry = false,
            EnableMetrics = false
        });
        ctx.AddLifecycleListener(listener);

        ctx.AddRoutes(r =>
        {
            r.From("direct://timeout-pipeline")
             .RouteId("timeout-pipeline")
             .ProcessingTimeout(TimeSpan.FromMilliseconds(50))
             .Process(async (e, ct) =>
             {
                 await Task.Delay(TimeSpan.FromSeconds(10), ct);
             });

            r.OnException<ExchangeTimedOutException>()
             .Process(e => e.ExceptionHandled = true);
        });

        await ctx.Start();

        var producer = ctx.GetEndpoint("direct://timeout-pipeline").CreateProducer();
        await producer.Start();

        var exchange = new Exchange(new Message("will-timeout"));
        await producer.Process(exchange, CancellationToken.None);

        await ctx.Stop();
        await ctx.DisposeAsync();

        // Assert — lifecycle listener received the timeout event
        timedOutExchangeIds.Should().Contain(exchange.ExchangeId);
        // Assert — exchange has the timeout properties set
        exchange.Exception.Should().BeOfType<ExchangeTimedOutException>();
        ((ExchangeTimedOutException)exchange.Exception!).RouteId.Should().Be("timeout-pipeline");
    }

    [Fact]
    public async Task ProcessingTimeout_InflightCleanedUp_AfterTimeout()
    {
        var ctx = new RouteContext(options: new RouteEngineOptions
        {
            EnableTelemetry = false,
            EnableMetrics = false
        });

        ctx.AddRoutes(r =>
        {
            r.From("direct://timeout-inflight")
             .RouteId("timeout-inflight")
             .ProcessingTimeout(TimeSpan.FromMilliseconds(50))
             .Process(async (e, ct) =>
             {
                 await Task.Delay(TimeSpan.FromSeconds(10), ct);
             });

            r.OnException<ExchangeTimedOutException>()
             .Process(e => e.ExceptionHandled = true);
        });

        await ctx.Start();

        var producer = ctx.GetEndpoint("direct://timeout-inflight").CreateProducer();
        await producer.Start();
        await producer.Process(new Exchange(new Message("timeout")), CancellationToken.None);

        // Assert — inflight was registered and unregistered despite timeout
        ctx.InflightRepository.Browse("timeout-inflight").Should().BeEmpty(
            "inflight entry should be cleaned up after timeout");

        await ctx.Stop();
        await ctx.DisposeAsync();
    }

    [Fact]
    public async Task ProcessingTimeout_NormalProcessing_NoTimeout()
    {
        var processed = false;

        var ctx = new RouteContext(options: new RouteEngineOptions
        {
            EnableTelemetry = false,
            EnableMetrics = false
        });

        ctx.AddRoutes(r =>
        {
            r.From("direct://fast-processing")
             .RouteId("fast-route")
             .ProcessingTimeout(TimeSpan.FromSeconds(5))
             .Process(e => { processed = true; });
        });

        await ctx.Start();

        var producer = ctx.GetEndpoint("direct://fast-processing").CreateProducer();
        await producer.Start();
        var exchange = new Exchange(new Message("fast"));
        await producer.Process(exchange, CancellationToken.None);

        // Assert — no timeout, clean processing
        processed.Should().BeTrue();
        exchange.Exception.Should().BeNull();
        ctx.InflightRepository.Browse("fast-route").Should().BeEmpty();

        await ctx.Stop();
        await ctx.DisposeAsync();
    }

    // ═══════════════════════════════════════════════════════════════════
    // Cross-phase: End-to-end graceful shutdown scenario
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task EndToEnd_GracefulShutdown_AllPhasesWork()
    {
        // Tests: ExchangeId tracking (P1), Drain (P2), ShutdownTimeout (P3),
        //        RouteStatus (P4), ProcessingTimeout (P5), Lifecycle events (P6)
        var listener = new FullLifecycleListener();

        var ctx = new RouteContext(options: new RouteEngineOptions
        {
            EnableTelemetry = false,
            EnableMetrics = false,
            ShutdownTimeout = TimeSpan.FromSeconds(5),
            DefaultProcessingTimeout = TimeSpan.FromSeconds(2)
        });
        ctx.AddLifecycleListener(listener);

        ctx.AddRoutes(r =>
        {
            r.From("direct://e2e-fast")
             .RouteId("e2e-fast")
             .Process(async (e, ct) =>
             {
                 await Task.Delay(50, ct);
             });

            r.From("direct://e2e-slow")
             .RouteId("e2e-slow")
             .ProcessingTimeout(TimeSpan.FromMilliseconds(80))
             .Process(async (e, ct) =>
             {
                 await Task.Delay(TimeSpan.FromSeconds(30), ct);
             });

            r.OnException<ExchangeTimedOutException>()
             .Process(e => e.ExceptionHandled = true);
        });

        await ctx.Start();

        // P4: RouteStatus — both started
        ctx.GetRoute("e2e-fast")!.Status.Should().Be(RouteStatus.Started);
        ctx.GetRoute("e2e-slow")!.Status.Should().Be(RouteStatus.Started);

        // P6: Lifecycle — started events
        listener.Events.Should().Contain("Started:e2e-fast");
        listener.Events.Should().Contain("Started:e2e-slow");

        var fastProducer = ctx.GetEndpoint("direct://e2e-fast").CreateProducer();
        await fastProducer.Start();
        var slowProducer = ctx.GetEndpoint("direct://e2e-slow").CreateProducer();
        await slowProducer.Start();

        // P1: ExchangeId — each exchange gets unique id
        var ex1 = new Exchange(new Message("fast"));
        var ex2 = new Exchange(new Message("slow"));
        ex1.ExchangeId.Should().NotBe(ex2.ExchangeId);

        // Send fast + slow messages
        await fastProducer.Process(ex1, CancellationToken.None);
        _ = slowProducer.Process(ex2, CancellationToken.None);
        await Task.Delay(20); // let slow start processing

        // P5: slow exchange should timeout
        await Task.Delay(200); // exceed the 80ms timeout
        // P6: timeout event should have fired
        listener.TimedOutIds.Should().NotBeEmpty("slow exchange should have timed out");

        // P2+P3: Stop should drain fast, force-cancel slow
        await ctx.Stop();

        // P2: inflight drained
        ctx.InflightRepository.Browse().Should().BeEmpty();

        // P6: lifecycle events (routes cleared after stop, but listener captured events)
        listener.Events.Should().Contain("Suspending:e2e-fast");
        listener.Events.Should().Contain("Stopped:e2e-fast");
        listener.Events.Should().Contain("Suspending:e2e-slow");
        listener.Events.Should().Contain("Stopped:e2e-slow");

        await ctx.DisposeAsync();
    }

    // ───── Helpers ─────

    private sealed class TimeoutTrackingListener : IRouteLifecycleListener
    {
        private readonly List<string> _timedOutIds;
        public TimeoutTrackingListener(List<string> timedOutIds) => _timedOutIds = timedOutIds;

        public Task OnExchangeTimedOut(string routeId, string exchangeId, TimeSpan elapsed, CancellationToken ct)
        {
            lock (_timedOutIds) _timedOutIds.Add(exchangeId);
            return Task.CompletedTask;
        }
    }

    private sealed class FullLifecycleListener : IRouteLifecycleListener
    {
        public List<string> Events { get; } = [];
        public List<string> TimedOutIds { get; } = [];

        public Task OnRouteStarted(string routeId, CancellationToken ct)
        { lock (Events) Events.Add($"Started:{routeId}"); return Task.CompletedTask; }

        public Task OnRouteStopped(string routeId, CancellationToken ct)
        { lock (Events) Events.Add($"Stopped:{routeId}"); return Task.CompletedTask; }

        public Task OnRouteSuspending(string routeId, CancellationToken ct)
        { lock (Events) Events.Add($"Suspending:{routeId}"); return Task.CompletedTask; }

        public Task OnRouteErrored(string routeId, Exception ex, CancellationToken ct)
        { lock (Events) Events.Add($"Errored:{routeId}"); return Task.CompletedTask; }

        public Task OnExchangeTimedOut(string routeId, string exchangeId, TimeSpan elapsed, CancellationToken ct)
        { lock (TimedOutIds) TimedOutIds.Add(exchangeId); return Task.CompletedTask; }
    }
}

using redb.Route.Abstractions;
using redb.Route.Configuration;
using redb.Route.Core;

namespace redb.Route.Tests.Core;

/// <summary>
/// Integration tests validating robustness fixes:
/// Start/Stop race conditions (#1), idempotency, restart safety (#8).
/// </summary>
public sealed class GracefulShutdownRobustnessTests
{
    // ═══════════════════════════════════════════════════════════════════
    // Fix #1: _routeLock — concurrent Start/Stop must not deadlock
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ConcurrentStartStop_DoesNotDeadlockOrThrow()
    {
        var ctx = new RouteContext(options: new RouteEngineOptions
        {
            EnableTelemetry = false,
            EnableMetrics = false,
            ShutdownTimeout = TimeSpan.FromSeconds(5)
        });

        ctx.AddRoutes(r =>
        {
            r.From("direct://race-test")
             .RouteId("race-route")
             .Process(async (e, ct) => await Task.Delay(10, ct));
        });

        var tasks = new List<Task>();
        for (int i = 0; i < 10; i++)
        {
            tasks.Add(Task.Run(async () =>
            {
                try { await ctx.Start(); }
                catch (OperationCanceledException) { }
            }));
            tasks.Add(Task.Run(async () =>
            {
                try { await ctx.Stop(); }
                catch (OperationCanceledException) { }
            }));
        }

        var allDone = Task.WhenAll(tasks);
        var completed = await Task.WhenAny(allDone, Task.Delay(TimeSpan.FromSeconds(30)));
        completed.Should().Be(allDone, "concurrent Start/Stop should complete without deadlock");

        await ctx.DisposeAsync();
    }

    [Fact]
    public async Task DoubleStart_IsIdempotent()
    {
        var ctx = new RouteContext(options: new RouteEngineOptions
        {
            EnableTelemetry = false,
            EnableMetrics = false
        });

        ctx.AddRoutes(r =>
        {
            r.From("direct://double-start")
             .RouteId("double-start")
             .Process(e => { });
        });

        await ctx.Start();
        await ctx.Start(); // second call — must be no-op

        ctx.GetRoute("double-start")!.Status.Should().Be(RouteStatus.Started);

        await ctx.Stop();
        await ctx.DisposeAsync();
    }

    [Fact]
    public async Task DoubleStop_IsIdempotent()
    {
        var ctx = new RouteContext(options: new RouteEngineOptions
        {
            EnableTelemetry = false,
            EnableMetrics = false
        });

        ctx.AddRoutes(r =>
        {
            r.From("direct://double-stop")
             .RouteId("double-stop")
             .Process(e => { });
        });

        await ctx.Start();
        await ctx.Stop();
        await ctx.Stop(); // second call — must not throw

        await ctx.DisposeAsync();
    }

    // ═══════════════════════════════════════════════════════════════════
    // Fix #8: Compile temp list — restart re‑compiles cleanly
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task StartStopRestart_RoutesWorkAcrossRestarts()
    {
        var processedCount = 0;

        var ctx = new RouteContext(options: new RouteEngineOptions
        {
            EnableTelemetry = false,
            EnableMetrics = false,
            ShutdownTimeout = TimeSpan.FromSeconds(5)
        });

        ctx.AddRoutes(r =>
        {
            r.From("direct://restart-test")
             .RouteId("restart-route")
             .Process(e => Interlocked.Increment(ref processedCount));
        });

        // First cycle
        await ctx.Start();
        var p1 = ctx.GetEndpoint("direct://restart-test").CreateProducer();
        await p1.Start();
        await p1.Process(new Exchange(new Message("msg1")));
        await ctx.Stop();

        // Second cycle — restart
        await ctx.Start();
        var p2 = ctx.GetEndpoint("direct://restart-test").CreateProducer();
        await p2.Start();
        await p2.Process(new Exchange(new Message("msg2")));
        await ctx.Stop();

        processedCount.Should().Be(2, "both messages should be processed across restart cycles");

        await ctx.DisposeAsync();
    }

    [Fact]
    public async Task StopUnderLoad_DrainsAllInflight_BeforeReturning()
    {
        var processedCount = 0;
        var allStarted = new CountdownEvent(5);

        var ctx = new RouteContext(options: new RouteEngineOptions
        {
            EnableTelemetry = false,
            EnableMetrics = false,
            ShutdownTimeout = TimeSpan.FromSeconds(10)
        });

        ctx.AddRoutes(r =>
        {
            r.From("direct://load-drain")
             .RouteId("load-drain")
             .Process(async (e, ct) =>
             {
                 allStarted.Signal();
                 await Task.Delay(300, ct);
                 Interlocked.Increment(ref processedCount);
             });
        });

        await ctx.Start();
        var producer = ctx.GetEndpoint("direct://load-drain").CreateProducer();
        await producer.Start();

        // Fire 5 concurrent messages
        var tasks = Enumerable.Range(0, 5)
            .Select(_ => producer.Process(new Exchange(new Message("msg")), CancellationToken.None))
            .ToArray();

        allStarted.Wait(TimeSpan.FromSeconds(5));

        // Stop while all 5 are in-flight
        await ctx.Stop();
        await Task.WhenAll(tasks);

        processedCount.Should().Be(5, "all in-flight exchanges must complete before Stop returns");
        ctx.InflightRepository.Browse().Should().BeEmpty();

        await ctx.DisposeAsync();
    }
}

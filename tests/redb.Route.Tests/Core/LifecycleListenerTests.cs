using redb.Route.Abstractions;
using redb.Route.Core;

namespace redb.Route.Tests.Core;

/// <summary>
/// Tests for Phase 6: Route Lifecycle Events.
/// </summary>
public sealed class LifecycleListenerTests
{
    private sealed class TestListener : IRouteLifecycleListener
    {
        public List<string> Events { get; } = [];

        public Task OnRouteStarted(string routeId, CancellationToken ct)
        {
            Events.Add($"Started:{routeId}");
            return Task.CompletedTask;
        }

        public Task OnRouteStopped(string routeId, CancellationToken ct)
        {
            Events.Add($"Stopped:{routeId}");
            return Task.CompletedTask;
        }

        public Task OnRouteSuspending(string routeId, CancellationToken ct)
        {
            Events.Add($"Suspending:{routeId}");
            return Task.CompletedTask;
        }

        public Task OnRouteErrored(string routeId, Exception ex, CancellationToken ct)
        {
            Events.Add($"Errored:{routeId}:{ex.GetType().Name}");
            return Task.CompletedTask;
        }

        public Task OnExchangeTimedOut(string routeId, string exchangeId, TimeSpan elapsed, CancellationToken ct)
        {
            Events.Add($"TimedOut:{routeId}:{exchangeId}");
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Listener_ReceivesStartedAndStopped_OnContextLifecycle()
    {
        var listener = new TestListener();
        var ctx = new RouteContext(options: new Configuration.RouteEngineOptions
        {
            EnableTelemetry = false,
            EnableMetrics = false
        });
        ctx.AddLifecycleListener(listener);

        ctx.AddRoutes(r =>
        {
            r.From("direct://lifecycle-test")
             .RouteId("lc-route")
             .Process(e => { });
        });

        await ctx.Start();
        await ctx.Stop();
        await ctx.DisposeAsync();

        listener.Events.Should().ContainInOrder(
            "Started:lc-route",
            "Suspending:lc-route",
            "Stopped:lc-route");
    }

    [Fact]
    public async Task Listener_ReceivesStartedAndStopped_OnIndividualRoute()
    {
        var listener = new TestListener();
        var ctx = new RouteContext(options: new Configuration.RouteEngineOptions
        {
            EnableTelemetry = false,
            EnableMetrics = false
        });
        ctx.AddLifecycleListener(listener);

        ctx.AddRoutes(r =>
        {
            r.From("direct://individual")
             .RouteId("ind-route")
             .AutoStart(false)
             .Process(e => { });
        });

        await ctx.Start();
        // Route not started yet (AutoStart=false)
        listener.Events.Should().BeEmpty();

        await ctx.StartRoute("ind-route");
        listener.Events.Should().Contain("Started:ind-route");

        await ctx.StopRoute("ind-route");
        listener.Events.Should().Contain("Suspending:ind-route");
        listener.Events.Should().Contain("Stopped:ind-route");

        await ctx.Stop();
        await ctx.DisposeAsync();
    }

    [Fact]
    public async Task MultipleListeners_AllReceiveEvents()
    {
        var listener1 = new TestListener();
        var listener2 = new TestListener();
        var ctx = new RouteContext(options: new Configuration.RouteEngineOptions
        {
            EnableTelemetry = false,
            EnableMetrics = false
        });
        ctx.AddLifecycleListener(listener1);
        ctx.AddLifecycleListener(listener2);

        ctx.AddRoutes(r =>
        {
            r.From("direct://multi")
             .RouteId("multi-route")
             .Process(e => { });
        });

        await ctx.Start();
        await ctx.Stop();
        await ctx.DisposeAsync();

        listener1.Events.Should().Contain("Started:multi-route");
        listener2.Events.Should().Contain("Started:multi-route");
        listener1.Events.Should().Contain("Stopped:multi-route");
        listener2.Events.Should().Contain("Stopped:multi-route");
    }

    [Fact]
    public async Task ListenerException_DoesNotBreakRouteLifecycle()
    {
        var goodListener = new TestListener();

        var ctx = new RouteContext(options: new Configuration.RouteEngineOptions
        {
            EnableTelemetry = false,
            EnableMetrics = false
        });

        // Bad listener that throws
        ctx.AddLifecycleListener(new ThrowingListener());
        ctx.AddLifecycleListener(goodListener);

        ctx.AddRoutes(r =>
        {
            r.From("direct://resilient")
             .RouteId("resilient-route")
             .Process(e => { });
        });

        // Should not throw despite bad listener
        await ctx.Start();
        await ctx.Stop();
        await ctx.DisposeAsync();

        // Good listener should still receive all events
        goodListener.Events.Should().Contain("Started:resilient-route");
        goodListener.Events.Should().Contain("Stopped:resilient-route");
    }

    [Fact]
    public async Task Listener_ReceivesSuspending_BeforeStopped()
    {
        var listener = new TestListener();
        var ctx = new RouteContext(options: new Configuration.RouteEngineOptions
        {
            EnableTelemetry = false,
            EnableMetrics = false
        });
        ctx.AddLifecycleListener(listener);

        ctx.AddRoutes(r =>
        {
            r.From("direct://order-test")
             .RouteId("order-route")
             .Process(e => { });
        });

        await ctx.Start();
        await ctx.Stop();
        await ctx.DisposeAsync();

        var suspendIdx = listener.Events.IndexOf("Suspending:order-route");
        var stoppedIdx = listener.Events.IndexOf("Stopped:order-route");

        suspendIdx.Should().BeGreaterThanOrEqualTo(0, "Suspending event should fire");
        stoppedIdx.Should().BeGreaterThanOrEqualTo(0, "Stopped event should fire");
        suspendIdx.Should().BeLessThan(stoppedIdx, "Suspending should fire before Stopped");
    }

    [Fact]
    public async Task Listener_ReceivesExchangeTimedOut()
    {
        var listener = new TestListener();
        var ctx = new RouteContext(options: new Configuration.RouteEngineOptions
        {
            EnableTelemetry = false,
            EnableMetrics = false
        });
        ctx.AddLifecycleListener(listener);

        ctx.AddRoutes(r =>
        {
            r.From("direct://timeout-lifecycle")
             .RouteId("timeout-lc")
             .ProcessingTimeout(TimeSpan.FromMilliseconds(50))
             .Process(async (e, ct) =>
             {
                 await Task.Delay(TimeSpan.FromSeconds(10), ct);
             });

            r.OnException<ExchangeTimedOutException>()
             .Process(e => e.ExceptionHandled = true);
        });

        await ctx.Start();

        var producer = ctx.GetEndpoint("direct://timeout-lifecycle").CreateProducer();
        await producer.Start();
        var exchange = new Exchange(new Message("test"));
        await producer.Process(exchange, CancellationToken.None);

        await ctx.Stop();
        await ctx.DisposeAsync();

        listener.Events.Should().Contain(e =>
            e.StartsWith("TimedOut:timeout-lc:"));
    }

    [Fact]
    public async Task DefaultInterfaceMethods_AreNoOp()
    {
        // IRouteLifecycleListener default methods should not throw
        IRouteLifecycleListener listener = new MinimalListener();

        await listener.OnRouteStarted("r1", CancellationToken.None);
        await listener.OnRouteStopped("r1", CancellationToken.None);
        await listener.OnRouteSuspending("r1", CancellationToken.None);
        await listener.OnRouteErrored("r1", new Exception("test"), CancellationToken.None);
        await listener.OnExchangeTimedOut("r1", "ex1", TimeSpan.FromSeconds(5), CancellationToken.None);
    }

    [Fact]
    public async Task LoggingLifecycleListener_DoesNotThrow()
    {
        var loggerFactory = Microsoft.Extensions.Logging.LoggerFactory.Create(b => { });
        var logger = loggerFactory.CreateLogger("test");
        var listener = new LoggingLifecycleListener(logger);

        await listener.OnRouteStarted("r1", CancellationToken.None);
        await listener.OnRouteStopped("r1", CancellationToken.None);
        await listener.OnRouteSuspending("r1", CancellationToken.None);
        await listener.OnRouteErrored("r1", new InvalidOperationException("boom"), CancellationToken.None);
        await listener.OnExchangeTimedOut("r1", "ex1", TimeSpan.FromSeconds(3), CancellationToken.None);
    }

    [Fact]
    public async Task AddLifecycleListener_ReturnsContextForChaining()
    {
        var ctx = new RouteContext(options: new Configuration.RouteEngineOptions
        {
            EnableTelemetry = false,
            EnableMetrics = false
        });

        var result = ctx.AddLifecycleListener(new TestListener());

        result.Should().BeSameAs(ctx);

        await ctx.DisposeAsync();
    }

    // ── Helpers ──

    private sealed class ThrowingListener : IRouteLifecycleListener
    {
        public Task OnRouteStarted(string routeId, CancellationToken ct)
            => throw new InvalidOperationException("Boom from listener!");

        public Task OnRouteStopped(string routeId, CancellationToken ct)
            => throw new InvalidOperationException("Boom from listener!");

        public Task OnRouteSuspending(string routeId, CancellationToken ct)
            => throw new InvalidOperationException("Boom from listener!");

        public Task OnRouteErrored(string routeId, Exception ex, CancellationToken ct)
            => throw new InvalidOperationException("Boom from listener!");
    }

    /// <summary>
    /// Implements IRouteLifecycleListener without overriding any methods — tests default members.
    /// </summary>
    private sealed class MinimalListener : IRouteLifecycleListener { }
}

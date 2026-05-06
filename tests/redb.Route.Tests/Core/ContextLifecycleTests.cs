using redb.Route.Abstractions;
using redb.Route.Core;

namespace redb.Route.Tests.Core;

/// <summary>
/// Tests for context-level lifecycle events: OnContextStarting, OnContextStarted,
/// OnContextStopping, OnContextStopped.
/// </summary>
public sealed class ContextLifecycleTests
{
    private sealed class ContextListener : IRouteLifecycleListener
    {
        public List<string> Events { get; } = [];

        public Task OnContextStarting(IRouteContext context, CancellationToken ct)
        { Events.Add("ContextStarting"); return Task.CompletedTask; }

        public Task OnContextStarted(IRouteContext context, CancellationToken ct)
        { Events.Add("ContextStarted"); return Task.CompletedTask; }

        public Task OnContextStopping(IRouteContext context, CancellationToken ct)
        { Events.Add("ContextStopping"); return Task.CompletedTask; }

        public Task OnContextStopped(IRouteContext context, CancellationToken ct)
        { Events.Add("ContextStopped"); return Task.CompletedTask; }

        public Task OnRouteStarted(string routeId, CancellationToken ct)
        { Events.Add($"RouteStarted:{routeId}"); return Task.CompletedTask; }

        public Task OnRouteStopped(string routeId, CancellationToken ct)
        { Events.Add($"RouteStopped:{routeId}"); return Task.CompletedTask; }

        public Task OnRouteSuspending(string routeId, CancellationToken ct)
        { Events.Add($"RouteSuspending:{routeId}"); return Task.CompletedTask; }

        public Task OnRouteErrored(string routeId, Exception ex, CancellationToken ct)
        { Events.Add($"RouteErrored:{routeId}"); return Task.CompletedTask; }

        public Task OnExchangeTimedOut(string routeId, string exchangeId, TimeSpan elapsed, CancellationToken ct)
            => Task.CompletedTask;
    }

    private static RouteContext CreateContext() => new(
        options: new Configuration.RouteEngineOptions
        {
            EnableTelemetry = false,
            EnableMetrics = false
        });

    [Fact]
    public async Task ContextStarting_FiresBeforeRouteStarted()
    {
        var listener = new ContextListener();
        var ctx = CreateContext();
        ctx.AddLifecycleListener(listener);

        ctx.AddRoutes(r =>
        {
            r.From("direct://ctx-start")
             .RouteId("cs-route")
             .Process(e => { });
        });

        await ctx.Start();
        await ctx.Stop();

        var startingIdx = listener.Events.IndexOf("ContextStarting");
        var routeStartedIdx = listener.Events.IndexOf("RouteStarted:cs-route");

        startingIdx.Should().BeGreaterThanOrEqualTo(0);
        routeStartedIdx.Should().BeGreaterThanOrEqualTo(0);
        startingIdx.Should().BeLessThan(routeStartedIdx,
            "ContextStarting should fire before any route starts");
    }

    [Fact]
    public async Task ContextStarted_FiresAfterRouteStarted()
    {
        var listener = new ContextListener();
        var ctx = CreateContext();
        ctx.AddLifecycleListener(listener);

        ctx.AddRoutes(r =>
        {
            r.From("direct://ctx-started")
             .RouteId("cs2-route")
             .Process(e => { });
        });

        await ctx.Start();
        await ctx.Stop();

        var routeStartedIdx = listener.Events.IndexOf("RouteStarted:cs2-route");
        var startedIdx = listener.Events.IndexOf("ContextStarted");

        routeStartedIdx.Should().BeGreaterThanOrEqualTo(0);
        startedIdx.Should().BeGreaterThanOrEqualTo(0);
        startedIdx.Should().BeGreaterThan(routeStartedIdx,
            "ContextStarted should fire after routes start");
    }

    [Fact]
    public async Task ContextStopping_FiresBeforeRouteStopped()
    {
        var listener = new ContextListener();
        var ctx = CreateContext();
        ctx.AddLifecycleListener(listener);

        ctx.AddRoutes(r =>
        {
            r.From("direct://ctx-stop")
             .RouteId("cstop-route")
             .Process(e => { });
        });

        await ctx.Start();
        await ctx.Stop();

        var stoppingIdx = listener.Events.IndexOf("ContextStopping");
        var routeSuspendingIdx = listener.Events.IndexOf("RouteSuspending:cstop-route");

        stoppingIdx.Should().BeGreaterThanOrEqualTo(0);
        routeSuspendingIdx.Should().BeGreaterThanOrEqualTo(0);
        stoppingIdx.Should().BeLessThan(routeSuspendingIdx,
            "ContextStopping should fire before routes start suspending");
    }

    [Fact]
    public async Task ContextStopped_FiresAfterRouteStopped()
    {
        var listener = new ContextListener();
        var ctx = CreateContext();
        ctx.AddLifecycleListener(listener);

        ctx.AddRoutes(r =>
        {
            r.From("direct://ctx-stopped")
             .RouteId("cstop2-route")
             .Process(e => { });
        });

        await ctx.Start();
        await ctx.Stop();

        var routeStoppedIdx = listener.Events.IndexOf("RouteStopped:cstop2-route");
        var stoppedIdx = listener.Events.IndexOf("ContextStopped");

        routeStoppedIdx.Should().BeGreaterThanOrEqualTo(0);
        stoppedIdx.Should().BeGreaterThanOrEqualTo(0);
        stoppedIdx.Should().BeGreaterThan(routeStoppedIdx,
            "ContextStopped should fire after all routes stopped");
    }

    [Fact]
    public async Task FullContextLifecycle_CorrectEventOrder()
    {
        var listener = new ContextListener();
        var ctx = CreateContext();
        ctx.AddLifecycleListener(listener);

        ctx.AddRoutes(r =>
        {
            r.From("direct://full-ctx")
             .RouteId("full-route")
             .Process(e => { });
        });

        await ctx.Start();
        await ctx.Stop();

        listener.Events.Should().ContainInOrder(
            "ContextStarting",
            "RouteStarted:full-route",
            "ContextStarted",
            "ContextStopping",
            "RouteSuspending:full-route",
            "RouteStopped:full-route",
            "ContextStopped");
    }

    [Fact]
    public async Task MultipleListeners_AllReceiveContextEvents()
    {
        var listener1 = new ContextListener();
        var listener2 = new ContextListener();
        var ctx = CreateContext();
        ctx.AddLifecycleListener(listener1);
        ctx.AddLifecycleListener(listener2);

        ctx.AddRoutes(r =>
        {
            r.From("direct://multi-ctx")
             .RouteId("multi-ctx-route")
             .Process(e => { });
        });

        await ctx.Start();
        await ctx.Stop();

        foreach (var listener in new[] { listener1, listener2 })
        {
            listener.Events.Should().Contain("ContextStarting");
            listener.Events.Should().Contain("ContextStarted");
            listener.Events.Should().Contain("ContextStopping");
            listener.Events.Should().Contain("ContextStopped");
        }
    }

    [Fact]
    public async Task ContextEvents_FireEvenWithNoRoutes()
    {
        var listener = new ContextListener();
        var ctx = CreateContext();
        ctx.AddLifecycleListener(listener);

        // No routes added
        await ctx.Start();
        await ctx.Stop();

        listener.Events.Should().Contain("ContextStarting");
        listener.Events.Should().Contain("ContextStarted");
        listener.Events.Should().Contain("ContextStopping");
        listener.Events.Should().Contain("ContextStopped");
    }
}

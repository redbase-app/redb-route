namespace redb.Route.Abstractions;

/// <summary>
/// Receives lifecycle events from <see cref="Core.RouteContext"/> when routes change state.
/// All methods have default no-op implementations — override only what you need.
/// </summary>
public interface IRouteLifecycleListener
{
    /// <summary>Fired after a route has been started and is processing exchanges.</summary>
    Task OnRouteStarted(string routeId, CancellationToken ct) => Task.CompletedTask;

    /// <summary>Fired after a route has been stopped.</summary>
    Task OnRouteStopped(string routeId, CancellationToken ct) => Task.CompletedTask;

    /// <summary>Fired when a route enters the Suspending state (drain starting).</summary>
    Task OnRouteSuspending(string routeId, CancellationToken ct) => Task.CompletedTask;

    /// <summary>Fired when a route fails to start or encounters a fatal error.</summary>
    Task OnRouteErrored(string routeId, Exception ex, CancellationToken ct) => Task.CompletedTask;

    /// <summary>Fired when an exchange times out (per-exchange processing timeout from Phase 5).</summary>
    Task OnExchangeTimedOut(string routeId, string exchangeId, TimeSpan elapsed, CancellationToken ct) => Task.CompletedTask;

    // ── Context-level events ──

    /// <summary>Fired before the context starts compiling and starting routes.</summary>
    Task OnContextStarting(IRouteContext context, CancellationToken ct) => Task.CompletedTask;

    /// <summary>Fired after the context has started all auto-start routes.</summary>
    Task OnContextStarted(IRouteContext context, CancellationToken ct) => Task.CompletedTask;

    /// <summary>Fired before the context begins stopping routes.</summary>
    Task OnContextStopping(IRouteContext context, CancellationToken ct) => Task.CompletedTask;

    /// <summary>Fired after the context has stopped all routes and cleaned up.</summary>
    Task OnContextStopped(IRouteContext context, CancellationToken ct) => Task.CompletedTask;
}

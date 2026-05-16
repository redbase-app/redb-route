using redb.Route.Core;

namespace redb.Route.Abstractions;

/// <summary>
/// Controls route lifecycle and receives per-exchange notifications.
/// Analogous to Apache Camel's <c>RoutePolicy</c>.
/// All methods have default no-op implementations — override only what you need.
/// </summary>
public interface IRoutePolicy
{
    // ── Lifecycle ──

    /// <summary>Called once after the route is compiled but before consumers start.</summary>
    Task OnInit(IRouteContext context, CompiledRoute route, CancellationToken ct) => Task.CompletedTask;

    /// <summary>Called before the route consumer is started. Throw to prevent startup.</summary>
    Task OnStart(IRouteContext context, CompiledRoute route, CancellationToken ct) => Task.CompletedTask;

    /// <summary>Called after the route consumer has been stopped.</summary>
    Task OnStop(IRouteContext context, CompiledRoute route, CancellationToken ct) => Task.CompletedTask;

    /// <summary>Called when the route is being removed from the context (final cleanup).</summary>
    Task OnRemove(IRouteContext context, CompiledRoute route, CancellationToken ct) => Task.CompletedTask;

    /// <summary>Called when the route is being suspended (drain starting).</summary>
    Task OnSuspend(IRouteContext context, CompiledRoute route, CancellationToken ct) => Task.CompletedTask;

    /// <summary>Called when the route is being resumed after suspension.</summary>
    Task OnResume(IRouteContext context, CompiledRoute route, CancellationToken ct) => Task.CompletedTask;

    // ── Metadata ──

    /// <summary>
    /// Returns policy-specific metadata for observability (UI, API, diagnostics).
    /// Keys are policy-defined; values should be JSON-serializable primitives.
    /// </summary>
    IReadOnlyDictionary<string, object>? GetMetadata() => null;

    // ── Per-exchange ──

    /// <summary>Called when an exchange starts processing in this route.</summary>
    Task OnExchangeBegin(IRouteContext context, IExchange exchange, CancellationToken ct) => Task.CompletedTask;

    /// <summary>Called when an exchange finishes processing (success or failure).</summary>
    Task OnExchangeDone(IRouteContext context, IExchange exchange, CancellationToken ct) => Task.CompletedTask;
}

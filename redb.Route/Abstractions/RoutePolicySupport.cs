using redb.Route.Core;

namespace redb.Route.Abstractions;

/// <summary>
/// Convenience base class for <see cref="IRoutePolicy"/> implementations.
/// Captures <see cref="Context"/> and <see cref="RouteId"/> in <see cref="OnInit"/>.
/// Override only the callbacks you need.
/// Analogous to Apache Camel's <c>RoutePolicySupport</c>.
/// </summary>
public abstract class RoutePolicySupport : IRoutePolicy
{
    /// <summary>Route context (available after <see cref="OnInit"/>).</summary>
    protected IRouteContext? Context { get; private set; }

    /// <summary>Route identifier (available after <see cref="OnInit"/>).</summary>
    protected string? RouteId { get; private set; }

    /// <inheritdoc />
    public virtual Task OnInit(IRouteContext context, CompiledRoute route, CancellationToken ct)
    {
        Context = context;
        RouteId = route.RouteId;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public virtual Task OnStart(IRouteContext context, CompiledRoute route, CancellationToken ct)
        => Task.CompletedTask;

    /// <inheritdoc />
    public virtual Task OnStop(IRouteContext context, CompiledRoute route, CancellationToken ct)
        => Task.CompletedTask;

    /// <inheritdoc />
    public virtual Task OnRemove(IRouteContext context, CompiledRoute route, CancellationToken ct)
        => Task.CompletedTask;

    /// <inheritdoc />
    public virtual Task OnSuspend(IRouteContext context, CompiledRoute route, CancellationToken ct)
        => Task.CompletedTask;

    /// <inheritdoc />
    public virtual Task OnResume(IRouteContext context, CompiledRoute route, CancellationToken ct)
        => Task.CompletedTask;

    /// <inheritdoc />
    public virtual IReadOnlyDictionary<string, object>? GetMetadata() => null;

    /// <inheritdoc />
    public virtual Task OnExchangeBegin(IRouteContext context, IExchange exchange, CancellationToken ct)
        => Task.CompletedTask;

    /// <inheritdoc />
    public virtual Task OnExchangeDone(IRouteContext context, IExchange exchange, CancellationToken ct)
        => Task.CompletedTask;
}

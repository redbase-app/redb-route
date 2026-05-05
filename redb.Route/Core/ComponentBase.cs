using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;

namespace redb.Route.Core;

/// <summary>
/// Base class for components. Provides scheme registration and endpoint creation contract.
/// Components MAY hold long-lived resources (connection pools, broker sessions);
/// override <see cref="DisposeAsync"/> to release them when <see cref="RouteContext"/> shuts down.
/// </summary>
public abstract class ComponentBase : IComponent, IAsyncDisposable
{
    /// <inheritdoc />
    public abstract string Scheme { get; }

    /// <inheritdoc />
    public virtual IReadOnlyList<string> AlternateSchemes => [];

    /// <summary>
    /// Route context that owns this component. Set automatically by <see cref="RouteContext.AddComponent"/>.
    /// Used by transport endpoints to resolve named connection factories from the registry.
    /// </summary>
    public IRouteContext? Context { get; internal set; }

    /// <summary>
    /// Logger for the component. Set automatically by <see cref="RouteContext.AddComponent"/>
    /// from the context's <see cref="ILoggerFactory"/>.
    /// </summary>
    public ILogger? Logger { get; internal set; }

    /// <inheritdoc />
    public abstract IEndpoint CreateEndpoint(EndpointUri uri);

    /// <summary>
    /// Releases component-owned long-lived resources. Default implementation is a no-op.
    /// Override in subclasses that hold connection pools, broker sessions, etc.
    /// Called by <see cref="RouteContext.DisposeAsync"/>.
    /// </summary>
    public virtual ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

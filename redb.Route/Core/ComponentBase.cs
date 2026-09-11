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
    /// Optional one-line path synonym for the structured XML form (Route-XML Ф0 §7.2):
    /// kafka → <c>topic</c>, rabbitmq → <c>queue</c>, file → <c>directory</c>. Read by the XML
    /// loader, the component catalog and the generated schema — never from a hand-kept list.
    /// A component without an override is addressed by the universal <c>path</c> attribute.
    /// </summary>
    public virtual string? StructuredPathSynonym => null;

    /// <summary>
    /// The path part of this component's URIs is a text body (sql: the query). The structured
    /// XML form then reads the path from the element's content (CDATA) rather than an attribute.
    /// </summary>
    public virtual bool PathIsText => false;

    /// <summary>
    /// Releases component-owned long-lived resources. Default implementation is a no-op.
    /// Override in subclasses that hold connection pools, broker sessions, etc.
    /// Called by <see cref="RouteContext.DisposeAsync"/>.
    /// </summary>
    public virtual ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

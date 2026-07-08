using redb.Route.Abstractions;
using redb.Route.Processors;

namespace redb.Route.Core;

/// <summary>
/// Represents a compiled route with lifecycle state (v2 — backed by the
/// canonical <see cref="IRouteDefinition"/> ProcessorDefinition tree).
/// </summary>
public sealed class CompiledRoute
{
    /// <summary>Creates a compiled route.</summary>
    public CompiledRoute(
        string routeId,
        string fromUri,
        IRouteDefinition definition,
        IProcessor pipeline,
        IConsumer consumer,
        IEndpoint endpoint)
    {
        RouteId = routeId;
        FromUri = fromUri;
        Definition = definition;
        Pipeline = pipeline;
        Consumer = consumer;
        Endpoint = endpoint;
    }

    /// <summary>Route identifier.</summary>
    public string RouteId { get; }

    /// <summary>Source endpoint URI.</summary>
    public string FromUri { get; }

    /// <summary>Original route definition (v2 tree).</summary>
    public IRouteDefinition Definition { get; }

    /// <summary>Compiled processor pipeline (root of the route's processor tree).</summary>
    public IProcessor Pipeline { get; }

    /// <summary>The consumer instance.</summary>
    public IConsumer Consumer { get; }

    /// <summary>The source endpoint.</summary>
    public IEndpoint Endpoint { get; }

    /// <summary>
    /// Whether this route should auto-start when the context starts.
    /// Can be overridden at runtime (e.g., by Tsak StateStore).
    /// </summary>
    public bool AutoStart { get; set; } = true;

    /// <summary>Route policy for lifecycle control, or <c>null</c> if none assigned.</summary>
    public IRoutePolicy? Policy { get; internal set; }

    /// <summary>
    /// Effective cluster-policy resolution captured at compile time. Never null;
    /// reports <c>"AllNodes"</c> when no policy was attached.
    /// </summary>
    public RoutePolicyDescriptor PolicyDescriptor { get; internal set; }
        = new(false, "AllNodes", null, "No policy attached");

    /// <summary>Current route status. Defaults to Stopped until started by the context.</summary>
    public RouteStatus Status { get; internal set; } = RouteStatus.Stopped;
}

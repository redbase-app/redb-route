using Microsoft.Extensions.DependencyInjection;

namespace redb.Route.Abstractions;

/// <summary>
/// Represents a lightweight endpoint in the route pipeline.
/// An endpoint is an addressable destination/source identified by a normalized URI key.
/// No metrics or health checks — those are handled by OpenTelemetry infrastructure.
/// </summary>
public interface IEndpoint
{
    /// <summary>Parsed URI identifying this endpoint.</summary>
    EndpointUri Uri { get; }

    /// <summary>The component that created this endpoint.</summary>
    IComponent Component { get; }

    /// <summary>Creates a producer for sending messages to this endpoint.</summary>
    IProducer CreateProducer();

    /// <summary>Creates a consumer for receiving messages from this endpoint.</summary>
    /// <param name="processor">The processor to invoke when a message arrives.</param>
    IConsumer CreateConsumer(IProcessor processor);

    /// <summary>Starts the endpoint lifecycle.</summary>
    /// <param name="ct">Cancellation token.</param>
    Task Start(CancellationToken ct = default);

    /// <summary>Stops the endpoint lifecycle.</summary>
    /// <param name="ct">Cancellation token.</param>
    Task Stop(CancellationToken ct = default);

    /// <summary>
    /// Optional scope factory for creating per-exchange DI scopes.
    /// Set by RouteContext during endpoint creation. Null when running without DI.
    /// </summary>
    IServiceScopeFactory? ScopeFactory { get => null; set { } }
}

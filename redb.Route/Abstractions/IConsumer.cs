namespace redb.Route.Abstractions;

/// <summary>
/// Receives messages from an endpoint and delegates to a processor.
/// Manages its own lifecycle (polling, subscribing, etc.).
/// </summary>
public interface IConsumer
{
    /// <summary>The endpoint this consumer is attached to (for diagnostics/logging).</summary>
    IEndpoint? Endpoint => null;

    /// <summary>Starts consuming messages.</summary>
    /// <param name="ct">Cancellation token.</param>
    Task Start(CancellationToken ct = default);

    /// <summary>Stops consuming messages.</summary>
    /// <param name="ct">Cancellation token.</param>
    Task Stop(CancellationToken ct = default);
}

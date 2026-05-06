namespace redb.Route.Abstractions;

/// <summary>
/// Sends messages to an endpoint. Extends IProcessor — producing is just processing.
/// </summary>
public interface IProducer : IProcessor
{
    /// <summary>Starts the producer.</summary>
    /// <param name="ct">Cancellation token.</param>
    Task Start(CancellationToken ct = default);

    /// <summary>Stops the producer.</summary>
    /// <param name="ct">Cancellation token.</param>
    Task Stop(CancellationToken ct = default);
}

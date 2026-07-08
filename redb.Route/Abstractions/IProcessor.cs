namespace redb.Route.Abstractions;

/// <summary>
/// Processes a message exchange asynchronously.
/// All processors in redb.Route are async-first with CancellationToken support.
/// </summary>
public interface IProcessor
{
    /// <summary>Processes the exchange asynchronously.</summary>
    /// <param name="exchange">The message exchange to process.</param>
    /// <param name="ct">Cancellation token for graceful shutdown.</param>
    Task Process(IExchange exchange, CancellationToken ct = default);
}

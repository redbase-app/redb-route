using Microsoft.Extensions.Logging;

namespace redb.Route.Core;

/// <summary>
/// Encapsulates inflight tracking and drain-on-stop logic.
/// Used by both poll-based (<see cref="DrainableConsumer"/>) and callback-based consumers
/// (RabbitMQ, MQTT, AMQP, TCP) to avoid duplicating CTS lifecycle + drain + force-cancel boilerplate.
/// </summary>
public sealed class InflightDrainGuard : IDisposable
{
    private CancellationTokenSource? _processingCts;
    private int _inflightCount;

    /// <summary>Drain timeout for graceful stop (default 30s).</summary>
    public TimeSpan DrainTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Current number of in-flight exchanges.</summary>
    public int InflightCount => Volatile.Read(ref _inflightCount);

    /// <summary>The processing cancellation token. Valid after <see cref="Start"/>.</summary>
    public CancellationToken ProcessingToken =>
        _processingCts?.Token ?? CancellationToken.None;

    /// <summary>
    /// Initializes the processing CTS linked to the application shutdown token.
    /// Safe to call multiple times — disposes the previous CTS if any.
    /// </summary>
    public void Start(CancellationToken ct = default)
    {
        _processingCts?.Dispose();
        _processingCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
    }

    /// <summary>Increments the inflight counter. Must be paired with <see cref="Decrement"/>.</summary>
    public void Increment() => Interlocked.Increment(ref _inflightCount);

    /// <summary>Decrements the inflight counter.</summary>
    public void Decrement() => Interlocked.Decrement(ref _inflightCount);

    /// <summary>
    /// Drains in-flight exchanges and force-cancels if drain times out.
    /// Call in consumer Stop() after stopping acceptance of new work.
    /// </summary>
    public async Task DrainAsync(CancellationToken ct, ILogger? logger = null, string? consumerName = null)
    {
        if (Volatile.Read(ref _inflightCount) > 0)
        {
            var drained = await DrainAwaiter.WaitAsync(
                () => Volatile.Read(ref _inflightCount),
                DrainTimeout, ct, logger,
                consumerName).ConfigureAwait(false);

            if (!drained && _processingCts != null)
            {
                logger?.LogWarning("[{Consumer}] Force-cancelling in-flight processing.", consumerName);
                await _processingCts.CancelAsync().ConfigureAwait(false);
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _processingCts?.Dispose();
        _processingCts = null;
    }
}

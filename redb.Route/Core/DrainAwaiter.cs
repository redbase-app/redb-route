using Microsoft.Extensions.Logging;

namespace redb.Route.Core;

/// <summary>
/// Utility for waiting until in-flight exchange count drops to zero.
/// Used by consumers during graceful Stop() to drain processing before shutdown.
/// </summary>
public static class DrainAwaiter
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan LogInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Waits until <paramref name="getInflightCount"/> returns 0, or <paramref name="timeout"/> elapses.
    /// </summary>
    /// <returns>true if drained successfully, false if timed out.</returns>
    public static async Task<bool> WaitAsync(
        Func<int> getInflightCount,
        TimeSpan timeout,
        CancellationToken ct,
        ILogger? logger = null,
        string? consumerName = null)
    {
        var count = getInflightCount();
        if (count <= 0)
            return true;

        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        var linkedToken = linked.Token;

        var lastLogTime = DateTime.UtcNow;

        logger?.LogInformation(
            "[{Consumer}] Waiting for {Count} in-flight exchange(s) to complete (timeout={Timeout}s)...",
            consumerName ?? "unknown", count, timeout.TotalSeconds);

        while (!linkedToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(PollInterval, linkedToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            count = getInflightCount();
            if (count <= 0)
            {
                logger?.LogInformation("[{Consumer}] All in-flight exchanges drained.", consumerName ?? "unknown");
                return true;
            }

            var now = DateTime.UtcNow;
            if (now - lastLogTime >= LogInterval)
            {
                logger?.LogInformation(
                    "[{Consumer}] Still waiting for {Count} in-flight exchange(s)...",
                    consumerName ?? "unknown", count);
                lastLogTime = now;
            }
        }

        count = getInflightCount();
        if (count <= 0)
            return true;

        logger?.LogWarning(
            "[{Consumer}] Drain timed out after {Timeout}s with {Count} in-flight exchange(s) remaining.",
            consumerName ?? "unknown", timeout.TotalSeconds, count);
        return false;
    }
}

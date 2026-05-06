using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.Telemetry;

namespace redb.Route.Processors;

/// <summary>
/// Limits the rate of exchange processing using a fixed-rate token release.
/// Exchanges beyond the rate limit are delayed until a slot becomes available.
/// Thread-safe for concurrent pipeline usage. Disposable — call <see cref="Dispose"/>
/// to cancel pending slot-release timers on shutdown.
/// </summary>
public sealed class ThrottleProcessor : IProcessor, IDisposable
{
    private readonly IProcessor _next;
    private readonly int _maxPerPeriod;
    private readonly TimeSpan _period;
    private readonly SemaphoreSlim _semaphore;
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly ILogger? _logger;
    private int _disposed;

    /// <summary>Creates a throttle processor.</summary>
    /// <param name="next">Next processor in the pipeline.</param>
    /// <param name="maxPerPeriod">Maximum number of exchanges allowed in the time period.</param>
    /// <param name="period">Time period for the rate limit (default: 1 second).</param>
    /// <param name="logger">Optional logger.</param>
    public ThrottleProcessor(IProcessor next, int maxPerPeriod, TimeSpan? period = null, ILogger? logger = null)
    {
        _next = next ?? throw new ArgumentNullException(nameof(next));
        if (maxPerPeriod <= 0) throw new ArgumentOutOfRangeException(nameof(maxPerPeriod), "Must be > 0.");
        _maxPerPeriod = maxPerPeriod;
        _period = period ?? TimeSpan.FromSeconds(1);
        _semaphore = new SemaphoreSlim(maxPerPeriod, maxPerPeriod);
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task Process(IExchange exchange, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);

        if (_semaphore.CurrentCount == 0)
        {
            ProcessorMetrics.ThrottleDelayed.Add(1);
            _logger?.LogDebug("Throttle: exchange delayed (all {Max} slots occupied).", _maxPerPeriod);
        }

        await _semaphore.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _next.Process(exchange, ct).ConfigureAwait(false);
        }
        finally
        {
            ScheduleSlotRelease();
        }
    }

    /// <summary>Cancels pending slot-release timers and disposes the semaphore.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _disposeCts.Cancel();
        _disposeCts.Dispose();
        _semaphore.Dispose();
    }

    private void ScheduleSlotRelease()
    {
        var token = _disposeCts.Token;
        _ = Task.Delay(_period, token).ContinueWith(_ =>
        {
            try { _semaphore.Release(); }
            catch (ObjectDisposedException) { /* Shutdown */ }
            catch (SemaphoreFullException) { /* Defensive: already released */ }
        }, CancellationToken.None, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
    }
}

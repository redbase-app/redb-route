using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.Telemetry;

namespace redb.Route.Processors;

/// <summary>
/// Per-key rate limiter: each key extracted from the exchange gets its own
/// independent <see cref="SemaphoreSlim"/>-based throttle.
/// Idle keys are lazily evicted after <c>2 × period</c> of inactivity.
/// Thread-safe for concurrent pipeline usage.
/// </summary>
public sealed class KeyedThrottleProcessor : IProcessor, IDisposable
{
    private readonly IProcessor _next;
    private readonly Func<IExchange, string> _keyExtractor;
    private readonly int _maxPerPeriod;
    private readonly TimeSpan _period;
    private readonly ConcurrentDictionary<string, KeyBucket> _buckets = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly ILogger? _logger;
    private int _disposed;

    /// <summary>Creates a keyed throttle processor.</summary>
    /// <param name="next">Next processor in the pipeline.</param>
    /// <param name="keyExtractor">Function extracting the throttle key from the exchange.</param>
    /// <param name="maxPerPeriod">Maximum exchanges per period per key.</param>
    /// <param name="period">Time period for the rate limit (default: 1 second).</param>
    /// <param name="logger">Optional logger.</param>
    public KeyedThrottleProcessor(
        IProcessor next,
        Func<IExchange, string> keyExtractor,
        int maxPerPeriod,
        TimeSpan? period = null,
        ILogger? logger = null)
    {
        _next = next ?? throw new ArgumentNullException(nameof(next));
        _keyExtractor = keyExtractor ?? throw new ArgumentNullException(nameof(keyExtractor));
        if (maxPerPeriod <= 0) throw new ArgumentOutOfRangeException(nameof(maxPerPeriod), "Must be > 0.");
        _maxPerPeriod = maxPerPeriod;
        _period = period ?? TimeSpan.FromSeconds(1);
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task Process(IExchange exchange, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);

        var key = _keyExtractor(exchange) ?? string.Empty;
        var bucket = _buckets.GetOrAdd(key, _ => new KeyBucket(_maxPerPeriod));

        bucket.Touch();

        if (bucket.Semaphore.CurrentCount == 0)
        {
            ProcessorMetrics.ThrottleDelayed.Add(1);
            _logger?.LogDebug("KeyedThrottle: exchange delayed for key '{Key}' (all {Max} slots occupied).", key, _maxPerPeriod);
        }

        await bucket.Semaphore.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _next.Process(exchange, ct).ConfigureAwait(false);
        }
        finally
        {
            ScheduleSlotRelease(bucket);
            TryEvictStale();
        }
    }

    /// <summary>Cancels pending timers and disposes all per-key semaphores.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _disposeCts.Cancel();
        _disposeCts.Dispose();
        foreach (var kvp in _buckets)
            kvp.Value.Semaphore.Dispose();
        _buckets.Clear();
    }

    private void ScheduleSlotRelease(KeyBucket bucket)
    {
        var token = _disposeCts.Token;
        _ = Task.Delay(_period, token).ContinueWith(_ =>
        {
            try { bucket.Semaphore.Release(); }
            catch (ObjectDisposedException) { /* Shutdown or evicted */ }
            catch (SemaphoreFullException) { /* Defensive */ }
        }, CancellationToken.None, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
    }

    private void TryEvictStale()
    {
        var threshold = Environment.TickCount64 - (long)(_period.TotalMilliseconds * 2);
        foreach (var kvp in _buckets)
        {
            if (kvp.Value.LastUsedTicks < threshold
                && kvp.Value.Semaphore.CurrentCount == _maxPerPeriod)
            {
                if (_buckets.TryRemove(kvp.Key, out var removed))
                    removed.Semaphore.Dispose();
            }
        }
    }

    private sealed class KeyBucket
    {
        public readonly SemaphoreSlim Semaphore;
        private long _lastUsedTicks;

        public KeyBucket(int maxCount)
        {
            Semaphore = new SemaphoreSlim(maxCount, maxCount);
            _lastUsedTicks = Environment.TickCount64;
        }

        public long LastUsedTicks => Volatile.Read(ref _lastUsedTicks);

        public void Touch() => Volatile.Write(ref _lastUsedTicks, Environment.TickCount64);
    }
}

using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.Telemetry;

namespace redb.Route.Processors;

/// <summary>
/// Per-key debounce: suppresses rapid-fire messages and forwards only the last
/// exchange for each key after a configurable quiet period.
/// When a new message arrives for a key that already has a pending timer,
/// the previous exchange is discarded and the timer is reset.
/// Thread-safe for concurrent pipeline usage.
/// </summary>
internal sealed class DebounceProcessor : IProcessor, IAsyncDisposable, IDisposable
{
    private readonly IProcessor _next;
    private readonly Func<IExchange, string> _keyExtractor;
    private readonly TimeSpan _quietPeriod;
    private readonly ILogger? _logger;
    private readonly ConcurrentDictionary<string, DebounceEntry> _entries = new(StringComparer.Ordinal);
    private int _disposed;

    /// <summary>Creates a debounce processor.</summary>
    /// <param name="next">Next processor in the pipeline.</param>
    /// <param name="keyExtractor">Function extracting the debounce key from the exchange.</param>
    /// <param name="quietPeriod">Duration of silence required before forwarding the last exchange.</param>
    /// <param name="logger">Optional logger for diagnostics.</param>
    public DebounceProcessor(
        IProcessor next,
        Func<IExchange, string> keyExtractor,
        TimeSpan quietPeriod,
        ILogger? logger = null)
    {
        _next = next ?? throw new ArgumentNullException(nameof(next));
        _keyExtractor = keyExtractor ?? throw new ArgumentNullException(nameof(keyExtractor));
        if (quietPeriod <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(quietPeriod), "Must be positive.");
        _quietPeriod = quietPeriod;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task Process(IExchange exchange, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);

        var key = _keyExtractor(exchange) ?? string.Empty;

        // Create or update the entry for this key
        _entries.AddOrUpdate(
            key,
            _ => CreateEntry(key, exchange, ct),
            (k, existing) =>
            {
                ProcessorMetrics.DebounceDiscarded.Add(1);
                existing.Reset(exchange, ct);
                ScheduleFlush(k, existing);
                return existing;
            });

        // Return immediately — the downstream call happens asynchronously after quiet period
        return Task.CompletedTask;
    }

    private DebounceEntry CreateEntry(string key, IExchange exchange, CancellationToken ct)
    {
        var entry = new DebounceEntry(exchange, ct);
        ScheduleFlush(key, entry);
        return entry;
    }

    private void ScheduleFlush(string key, DebounceEntry entry)
    {
        var cts = entry.Cts;
        _ = Task.Delay(_quietPeriod, cts.Token).ContinueWith(async _ =>
        {
            if (_entries.TryRemove(key, out var removed))
            {
                try
                {
                    await _next.Process(removed.Exchange, removed.UpstreamCt).ConfigureAwait(false);
                    ProcessorMetrics.DebounceFlushed.Add(1);
                }
                catch (Exception ex)
                {
                    removed.Exchange.Exception = ex;
                    _logger?.LogError(ex, "Debounce flush failed for key '{Key}'", key);
                }
                finally
                {
                    removed.Dispose();
                }
            }
        }, CancellationToken.None, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
    }

    /// <summary>Flush all pending entries immediately (used during graceful shutdown).</summary>
    public async Task FlushAsync()
    {
        var keys = _entries.Keys.ToArray();
        foreach (var key in keys)
        {
            if (_entries.TryRemove(key, out var entry))
            {
                try
                {
                    await _next.Process(entry.Exchange, entry.UpstreamCt).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    entry.Exchange.Exception = ex;
                    _logger?.LogError(ex, "Debounce flush failed for key '{Key}'", key);
                }
                finally
                {
                    entry.Dispose();
                }
            }
        }
    }

    /// <summary>Cancels pending timers and disposes all entries.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        foreach (var kvp in _entries)
        {
            kvp.Value.Dispose();
        }
        _entries.Clear();
    }

    /// <summary>Flushes pending entries and disposes.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await FlushAsync().ConfigureAwait(false);
        _entries.Clear();
    }

    private sealed class DebounceEntry : IDisposable
    {
        private CancellationTokenSource _cts;
        private IExchange _exchange;
        private CancellationToken _upstreamCt;

        public DebounceEntry(IExchange exchange, CancellationToken upstreamCt)
        {
            _cts = new CancellationTokenSource();
            _exchange = exchange;
            _upstreamCt = upstreamCt;
        }

        public CancellationTokenSource Cts => Volatile.Read(ref _cts);
        public IExchange Exchange => Volatile.Read(ref _exchange);
        public CancellationToken UpstreamCt => _upstreamCt;

        /// <summary>Replaces the stored exchange and resets the timer.</summary>
        public void Reset(IExchange newExchange, CancellationToken ct)
        {
            var oldCts = Interlocked.Exchange(ref _cts, new CancellationTokenSource());
            try { oldCts.Cancel(); } catch (ObjectDisposedException) { }
            oldCts.Dispose();
            Volatile.Write(ref _exchange, newExchange);
            _upstreamCt = ct;
        }

        public void Dispose()
        {
            try { _cts.Cancel(); } catch (ObjectDisposedException) { }
            _cts.Dispose();
        }
    }
}

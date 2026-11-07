using redb.Route.Abstractions;

namespace redb.Route.Processors;

/// <summary>
/// Resequences exchanges based on a sequence key, delivering them in order.
/// Uses batch mode: collects exchanges until a batch is complete (by size or timeout),
/// then sorts and delivers them. An active timer ensures buffered messages are flushed
/// even when no new messages arrive.
/// Thread-safe for concurrent pipeline usage. Implements <see cref="IAsyncDisposable"/>
/// to release the timer and semaphore.
/// </summary>
public sealed class ResequencerProcessor : IProcessor, IAsyncDisposable
{
    private readonly IProcessor _next;
    private readonly Func<IExchange, long> _keySelector;
    private readonly int _batchSize;
    private readonly TimeSpan _timeout;
    private readonly List<(long Key, IExchange Exchange)> _buffer = [];
    private readonly SemaphoreSlim _bufferLock = new(1, 1);
    private readonly CancellationTokenSource _disposeCts = new();
    private Timer? _timer;
    private int _disposed;

    /// <summary>Creates a resequencer processor.</summary>
    /// <param name="next">Next processor in the pipeline (receives ordered exchanges).</param>
    /// <param name="keySelector">Function to extract the sequence key (lower = earlier).</param>
    /// <param name="batchSize">Max exchanges before flushing the batch.</param>
    /// <param name="timeout">Max time to wait for a complete batch.</param>
    public ResequencerProcessor(
        IProcessor next,
        Func<IExchange, long> keySelector,
        int batchSize = 100,
        TimeSpan? timeout = null)
    {
        _next = next ?? throw new ArgumentNullException(nameof(next));
        _keySelector = keySelector ?? throw new ArgumentNullException(nameof(keySelector));
        if (batchSize <= 0) throw new ArgumentOutOfRangeException(nameof(batchSize));
        _batchSize = batchSize;
        _timeout = timeout ?? TimeSpan.FromSeconds(5);
    }

    /// <inheritdoc />
    public async Task Process(IExchange exchange, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);

        List<(long Key, IExchange Exchange)>? toFlush = null;

        await _bufferLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var key = _keySelector(exchange);
            _buffer.Add((key, exchange));

            if (_buffer.Count >= _batchSize)
            {
                toFlush = [.. _buffer];
                _buffer.Clear();
                StopTimer();
            }
            else if (_buffer.Count == 1)
            {
                // First item in a new batch — start the active timer
                StartTimer();
            }
        }
        finally
        {
            _bufferLock.Release();
        }

        if (toFlush is not null)
        {
            await FlushBatch(toFlush, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Forces a flush of any buffered exchanges. Call this when shutting down
    /// to process remaining exchanges.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    public async Task Flush(CancellationToken ct = default)
    {
        List<(long Key, IExchange Exchange)>? toFlush = null;

        await _bufferLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_buffer.Count > 0)
            {
                toFlush = [.. _buffer];
                _buffer.Clear();
                StopTimer();
            }
        }
        finally
        {
            _bufferLock.Release();
        }

        if (toFlush is not null)
        {
            await FlushBatch(toFlush, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Gets the number of currently buffered exchanges.</summary>
    public async Task<int> GetBufferedCount(CancellationToken ct = default)
    {
        await _bufferLock.WaitAsync(ct).ConfigureAwait(false);
        try { return _buffer.Count; }
        finally { _bufferLock.Release(); }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await _disposeCts.CancelAsync().ConfigureAwait(false);
        if (_timer is not null) await _timer.DisposeAsync().ConfigureAwait(false);
        _disposeCts.Dispose();
        _bufferLock.Dispose();
    }

    private void StartTimer()
    {
        // One-shot timer that fires after timeout
        _timer?.Change(Timeout.Infinite, Timeout.Infinite); // stop previous if any
        _timer ??= new Timer(OnTimerFired, null, Timeout.Infinite, Timeout.Infinite);
        _timer.Change(_timeout, Timeout.InfiniteTimeSpan);
    }

    private void StopTimer()
    {
        _timer?.Change(Timeout.Infinite, Timeout.Infinite);
    }

    private void OnTimerFired(object? state)
    {
        // Timer callback — flush on the thread pool. Fire-and-forget is acceptable here
        // because the timer is a best-effort mechanism. Exceptions from _next.Process
        // are set on individual exchanges.
        _ = Flush(_disposeCts.Token);
    }

    private async Task FlushBatch(List<(long Key, IExchange Exchange)> batch, CancellationToken ct)
    {
        // Sort by key ascending (sequence order)
        batch.Sort((a, b) => a.Key.CompareTo(b.Key));

        foreach (var (_, exchange) in batch)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await _next.Process(exchange, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                exchange.Exception = ex;
            }
            if (exchange.IsStopped) break;
        }
    }
}

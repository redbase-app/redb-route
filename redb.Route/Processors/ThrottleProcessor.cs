using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.Telemetry;

namespace redb.Route.Processors;

/// <summary>
/// Limits the rate of exchange processing using a fixed-rate token release.
/// Thread-safe for concurrent pipeline usage. Disposable — call <see cref="Dispose"/>
/// to cancel pending slot-release timers on shutdown.
/// <para>
/// Two overflow modes (selected via the <c>rejectOnOverflow</c> constructor flag):
/// </para>
/// <list type="bullet">
///   <item><c>false</c> (default, legacy) — semaphore-wait until a slot frees; the calling
///   exchange is blocked but eventually proceeds. Preserves backward compatibility.</item>
///   <item><c>true</c> (RFC 6585) — reject overflow exchanges immediately with HTTP 429
///   Too Many Requests and a <c>Retry-After</c> header (RFC 7231 §7.1.3) set to the
///   current rate-limit period. Strongly recommended for any HTTP-facing endpoint so
///   the client can back off explicitly instead of seeing what looks like a hung server.</item>
/// </list>
/// </summary>
public sealed class ThrottleProcessor : IProcessor, IDisposable
{
    private readonly IProcessor _next;
    private readonly Func<IExchange, int> _maxPerPeriod;
    private readonly TimeSpan _period;
    private readonly bool _rejectOnOverflow;
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly ILogger? _logger;
    private int _disposed;

    // The gate. A semaphore cannot change capacity per exchange, so the window is tracked by
    // hand: how many slots are occupied, and who is waiting for one. Waiters are served in
    // arrival order; each carries the limit its own exchange evaluated to.
    private readonly object _gate = new();
    private int _occupied;
    private readonly Queue<(int Limit, TaskCompletionSource<bool> Slot)> _waiters = new();

    /// <summary>No slot occupied and nobody waiting — a per-key gate in this state can be evicted.</summary>
    internal bool IsIdle
    {
        get { lock (_gate) return _occupied == 0 && _waiters.Count == 0; }
    }

    /// <summary>Creates a throttle processor with a fixed limit.</summary>
    /// <param name="next">Next processor in the pipeline.</param>
    /// <param name="maxPerPeriod">Maximum number of exchanges allowed in the time period.</param>
    /// <param name="period">Time period for the rate limit (default: 1 second).</param>
    /// <param name="rejectOnOverflow">When <c>true</c>, exchanges that exceed the rate limit are
    /// short-circuited with HTTP 429 + <c>Retry-After</c> instead of waiting.</param>
    /// <param name="logger">Optional logger.</param>
    public ThrottleProcessor(
        IProcessor next,
        int maxPerPeriod,
        TimeSpan? period = null,
        bool rejectOnOverflow = false,
        ILogger? logger = null)
        : this(next, _ => maxPerPeriod, period, rejectOnOverflow, logger)
    {
        if (maxPerPeriod <= 0) throw new ArgumentOutOfRangeException(nameof(maxPerPeriod), "Must be > 0.");
    }

    /// <summary>
    /// Creates a throttle processor whose limit is computed per exchange. The factory runs on
    /// every message, so the limit can come from a header, a property or an expression and change
    /// between messages, as the Apache Camel throttler does with a dynamic expression.
    /// </summary>
    /// <param name="next">Next processor in the pipeline.</param>
    /// <param name="maxPerPeriod">Computes the maximum number of exchanges allowed in the period
    /// for the given exchange. Must return a positive number; anything else fails that exchange
    /// loudly rather than silently letting everything through.</param>
    /// <param name="period">Time period for the rate limit (default: 1 second).</param>
    /// <param name="rejectOnOverflow">When <c>true</c>, exchanges that exceed the rate limit are
    /// short-circuited with HTTP 429 + <c>Retry-After</c> instead of waiting.</param>
    /// <param name="logger">Optional logger.</param>
    public ThrottleProcessor(
        IProcessor next,
        Func<IExchange, int> maxPerPeriod,
        TimeSpan? period = null,
        bool rejectOnOverflow = false,
        ILogger? logger = null)
    {
        _next = next ?? throw new ArgumentNullException(nameof(next));
        _maxPerPeriod = maxPerPeriod ?? throw new ArgumentNullException(nameof(maxPerPeriod));
        _period = period ?? TimeSpan.FromSeconds(1);
        _rejectOnOverflow = rejectOnOverflow;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task Process(IExchange exchange, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);

        var limit = _maxPerPeriod(exchange);
        if (limit <= 0)
            throw new InvalidOperationException(
                $"Throttle limit evaluated to {limit} for this exchange; it must be a positive number.");

        // Non-blocking probe so we can choose between rejection and waiting without
        // holding a slot speculatively.
        if (!TryAcquire(limit))
        {
            ProcessorMetrics.ThrottleDelayed.Add(1);
            if (_rejectOnOverflow)
            {
                _logger?.LogDebug("Throttle: rejecting overflow with 429 (RFC 6585).");
                ThrottleRejection.Apply(exchange, _period);
                return;
            }
            _logger?.LogDebug("Throttle: exchange delayed (all {Max} slots occupied).", limit);
            await WaitForSlotAsync(limit, ct).ConfigureAwait(false);
        }
        try
        {
            await _next.Process(exchange, ct).ConfigureAwait(false);
        }
        finally
        {
            ScheduleSlotRelease();
        }
    }

    /// <summary>Cancels pending slot-release timers and wakes every waiter with a cancellation.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _disposeCts.Cancel();
        _disposeCts.Dispose();
        lock (_gate)
        {
            while (_waiters.Count > 0)
                _waiters.Dequeue().Slot.TrySetCanceled();
        }
    }

    private bool TryAcquire(int limit)
    {
        lock (_gate)
        {
            // Arrival order is honoured: a newcomer does not overtake a queued exchange even if
            // its own limit would allow it in right now.
            if (_waiters.Count == 0 && _occupied < limit)
            {
                _occupied++;
                return true;
            }
            return false;
        }
    }

    private async Task WaitForSlotAsync(int limit, CancellationToken ct)
    {
        var slot = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            _waiters.Enqueue((limit, slot));
        }

        using var registration = ct.Register(static state => ((TaskCompletionSource<bool>)state!).TrySetCanceled(), slot);
        try
        {
            await slot.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The slot may have been granted just before cancellation won the race; give it back.
            lock (_gate)
            {
                if (slot.Task.IsCompletedSuccessfully)
                    _occupied--;
            }
            throw;
        }
    }

    private void ScheduleSlotRelease()
    {
        // Disposed while a message was in flight (a keyed gate evicted, a route stopped): there is no
        // window left to release into, and the token source is gone.
        if (Volatile.Read(ref _disposed) != 0) return;
        CancellationToken token;
        try { token = _disposeCts.Token; }
        catch (ObjectDisposedException) { return; }
        _ = Task.Delay(_period, token).ContinueWith(_ => ReleaseSlot(),
            CancellationToken.None, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
    }

    private void ReleaseSlot()
    {
        lock (_gate)
        {
            _occupied--;

            // Hand the freed capacity to the head of the queue, and keep going while the next
            // waiter's own limit still has room: after a larger limit arrives several may fit.
            while (_waiters.Count > 0 && _occupied < _waiters.Peek().Limit)
            {
                var (_, slot) = _waiters.Dequeue();
                if (slot.TrySetResult(true))
                    _occupied++;
            }
        }
    }
}

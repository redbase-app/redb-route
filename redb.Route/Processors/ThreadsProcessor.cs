using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;

namespace redb.Route.Processors;

/// <summary>
/// <b>Threads EIP</b> — Camel-style concurrency stage. Hands each incoming exchange to a bounded pool
/// of <c>poolSize</c> persistent workers that run the child pipeline (the <c>body</c>), capping the
/// section's concurrency at <c>poolSize</c>.
/// <para>
/// <b>Adaptive by exchange pattern</b> (one API, transparent the way Camel's <c>threads()</c> is):
/// <list type="bullet">
///   <item><b>InOnly</b> — fire-and-forget hand-off: the caller returns as soon as the exchange is
///   accepted, so a strictly serial polling consumer (file / sql / imap / …) can dispatch the next
///   item while the pool processes the current one — up to <c>poolSize</c> in parallel. This is the
///   general-purpose alternative to <c>.To("seda://x").ConcurrentConsumers(N)</c>, without a named
///   endpoint. The exchange is <b>cloned</b> (own DI scope), enqueued to the worker pool, and the
///   worker (started under <see cref="ExecutionContext.SuppressFlow"/> — a <b>transaction boundary</b>,
///   like <c>.To("seda://")</c>) owns and disposes the clone.</item>
///   <item><b>InOut</b> — request/reply: the body runs <b>inline on the SAME exchange</b> under a
///   <see cref="SemaphoreSlim"/> gate (≤ <c>poolSize</c> concurrent; the rest wait for a permit).
///   Nothing is cloned or copied, so <c>In</c> / <c>Out</c> / headers / properties and the reply are
///   preserved exactly — <b>lossless</b> regardless of whether the route writes its response to
///   <c>Out</c> or to <c>In</c> (redb.Route's HTTP consumer falls back to <c>In</c> when <c>Out</c> is
///   unset). This is <b>not</b> a transaction boundary — the ambient <c>TransactionScope</c> flows into
///   the body, which is correct for request/reply (the caller awaits the result anyway). Exceptions
///   propagate up to the route's outer OnException wrapper exactly as an un-threaded inline route, so
///   error handling / <c>Handled()</c> is unchanged.</item>
/// </list>
/// </para>
/// <para>
/// <b>Backpressure:</b> InOnly waits on the bounded hand-off queue; InOut waits on the gate. Both honour
/// <see cref="EnqueueTimeout"/> — bound the wait for a free slot instead of waiting indefinitely
/// (on timeout, <see cref="TimeoutException"/>; for InOut that surfaces to the awaiting caller).
/// </para>
/// <para>
/// <b>Drain-on-stop:</b> the processor registers itself as an <see cref="IRouteLifecycleListener"/>;
/// on context stop it stops accepting, completes the queue, awaits the workers (bounded by
/// <see cref="DrainTimeout"/>) and force-cancels only if that window is exceeded. A straggler exchange
/// that arrives after the queue is completed is run <em>inline</em> so it is never dropped.
/// </para>
/// <para><b>Ordering is not preserved</b> when <c>poolSize &gt; 1</c> (documented, same as brokers).</para>
/// </summary>
public sealed class ThreadsProcessor : IProcessor, IRouteLifecycleListener
{
    private readonly IProcessor _body;
    private readonly int _poolSize;
    private readonly IRouteContext _context;
    private readonly ILogger? _logger;
    private readonly Channel<IExchange> _queue;   // InOnly hand-off queue (worker pool)
    private readonly SemaphoreSlim _gate;          // InOut concurrency gate (inline, capped at poolSize)
    private readonly CancellationTokenSource _workerCts = new();
    private Task[]? _workers;
    private int _started;
    private volatile bool _shuttingDown;
    private long _processedCount;

    /// <summary>Graceful-drain window on stop before workers are force-cancelled (default 30s).</summary>
    public TimeSpan DrainTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Optional cap on how long a caller waits for a free worker slot when the pool + queue are
    /// saturated. <c>null</c> (default) = wait indefinitely (backpressure). When set and the window
    /// elapses, the enqueue fails with <see cref="TimeoutException"/> — surfaced to the caller
    /// (for InOut/RPC that becomes the reply fault instead of an unbounded hang).
    /// </summary>
    public TimeSpan? EnqueueTimeout { get; init; }

    /// <summary>Total number of exchanges processed across all workers.</summary>
    public long ProcessedCount => Interlocked.Read(ref _processedCount);

    /// <summary>Number of concurrent workers (the effective max degree of parallelism).</summary>
    public int PoolSize => _poolSize;

    /// <summary>Creates a Threads processor.</summary>
    /// <param name="body">Compiled child pipeline run by each worker.</param>
    /// <param name="poolSize">Number of concurrent workers (must be &gt;= 1).</param>
    /// <param name="maxQueueSize">Bounded hand-off queue capacity (backpressure). 0 = default (== poolSize).</param>
    /// <param name="context">Route context (used for error routing + lifecycle/drain registration).</param>
    /// <param name="logger">Optional logger.</param>
    public ThreadsProcessor(IProcessor body, int poolSize, int maxQueueSize, IRouteContext context, ILogger? logger = null)
    {
        _body = body ?? throw new ArgumentNullException(nameof(body));
        if (poolSize < 1)
            throw new ArgumentOutOfRangeException(nameof(poolSize), poolSize, "poolSize must be at least 1.");
        if (maxQueueSize < 0)
            throw new ArgumentOutOfRangeException(nameof(maxQueueSize), maxQueueSize, "maxQueueSize cannot be negative.");

        _poolSize = poolSize;
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _logger = logger;

        var capacity = maxQueueSize > 0 ? maxQueueSize : poolSize;
        _queue = Channel.CreateBounded<IExchange>(new BoundedChannelOptions(capacity)
        {
            // Backpressure: when the pool + queue are saturated the producer (the caller's Process)
            // asynchronously waits, throttling a fast poll loop to the pool's capacity.
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false
        });

        // InOut path runs the body inline under this gate — at most poolSize bodies concurrently.
        _gate = new SemaphoreSlim(poolSize, poolSize);

        _context.AddLifecycleListener(this);
    }

    /// <inheritdoc />
    public async Task Process(IExchange exchange, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(exchange);

        // Adaptive by exchange pattern, so .Threads() is transparent the way Camel's threads() is —
        // one API, no separate primitive:
        //  • InOut  → request/reply. Runs the body INLINE on the SAME exchange under a semaphore gate:
        //             at most poolSize bodies at once, the rest wait for a permit. Nothing is cloned or
        //             copied, so In / Out / headers / properties / the reply are preserved exactly
        //             (lossless regardless of whether the route writes its response to Out or In). This
        //             is NOT a transaction boundary — the ambient TransactionScope flows into the body,
        //             which is correct for request/reply (the caller awaits the result anyway). Errors
        //             propagate up the pipeline to the outer OnException wrapper, exactly as without
        //             .Threads(), so RPC error handling is unchanged.
        //  • InOnly → fire-and-forget hand-off: clone (own DI scope), enqueue and return so a serial
        //             polling consumer keeps pumping up to poolSize exchanges concurrently. The worker
        //             runs the body under ExecutionContext.SuppressFlow (a transaction boundary, like
        //             .To("seda://")) and owns + disposes the clone.
        if (exchange.Pattern == ExchangePattern.InOut)
            await ProcessInOut(exchange, ct).ConfigureAwait(false);
        else
            await ProcessInOnly(exchange, ct).ConfigureAwait(false);
    }

    private async Task ProcessInOut(IExchange exchange, CancellationToken ct)
    {
        // Concurrency gate: acquire a permit (≤ poolSize in flight), run the body inline on the SAME
        // exchange, release. No clone / no copy-back → the reply is whatever the body produced, in place.
        if (!await AcquireAsync(ct).ConfigureAwait(false))
            throw new TimeoutException(
                $"Threads gate: no free slot within {EnqueueTimeout} (poolSize={_poolSize}).");
        try
        {
            // Exceptions propagate up to the route's outer OnException wrapper (see RouteContext) — the
            // same path as an un-threaded inline route, so error handling / Handled() is unchanged.
            await _body.Process(exchange, ct).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Increment(ref _processedCount);
            _gate.Release();
        }
    }

    private async Task ProcessInOnly(IExchange exchange, CancellationToken ct)
    {
        EnsureStarted();

        // Clone (own DI scope, owned by the worker) and enqueue; the caller returns immediately and its
        // poll loop can dispatch the next exchange. The bounded queue applies backpressure at capacity.
        var copy = exchange.Clone();

        if (_shuttingDown)
        {
            // Queue is being torn down — do not lose this straggler; run it inline on the caller.
            await RunBody(copy, ct).ConfigureAwait(false);
            return;
        }

        try
        {
            await EnqueueAsync(copy, ct).ConfigureAwait(false);
        }
        catch (ChannelClosedException)
        {
            // Raced with shutdown between the _shuttingDown check and the write — run inline, don't drop.
            // RunBody's finally disposes the copy (releasing its per-exchange DI scope / DB connection).
            await RunBody(copy, ct).ConfigureAwait(false);
        }
        catch
        {
            // Enqueue failed (cancellation under backpressure / EnqueueTimeout) and the copy was neither
            // enqueued nor run inline — dispose it here so its per-exchange DI scope / DB connection is
            // released, then rethrow so the caller still observes the cancellation/fault.
            await copy.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task EnqueueAsync(IExchange item, CancellationToken ct)
    {
        if (EnqueueTimeout is not { } timeout)
        {
            await _queue.Writer.WriteAsync(item, ct).ConfigureAwait(false);
            return;
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        try
        {
            await _queue.Writer.WriteAsync(item, timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Threads pool: no free worker slot within {timeout} (poolSize={_poolSize}, queue saturated).");
        }
    }

    private async Task<bool> AcquireAsync(CancellationToken ct)
    {
        if (EnqueueTimeout is not { } timeout)
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            return true;
        }
        // Returns false on timeout (throws only on cancellation) — the caller maps false to TimeoutException.
        return await _gate.WaitAsync(timeout, ct).ConfigureAwait(false);
    }

    private void EnsureStarted()
    {
        if (Interlocked.CompareExchange(ref _started, 1, 0) != 0) return;

        _workers = new Task[_poolSize];
        // Suppress ExecutionContext flow so the persistent workers never capture the first caller's
        // ambient TransactionScope / AsyncLocal state — .Threads() is a clean async boundary.
        using (ExecutionContext.SuppressFlow())
        {
            for (var i = 0; i < _poolSize; i++)
                _workers[i] = Task.Run(() => WorkerLoop(_workerCts.Token));
        }
    }

    private async Task WorkerLoop(CancellationToken ct)
    {
        try
        {
            await foreach (var exchange in _queue.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                await RunBody(exchange, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (ChannelClosedException) { }
    }

    private async Task RunBody(IExchange exchange, CancellationToken ct)
    {
        try
        {
            await _body.Process(exchange, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Shutting down / cancelled — not a business error, do not route to OnException.
        }
        catch (Exception ex)
        {
            // Detached from the caller: route the failure to the route's error handling explicitly,
            // since the ancestor OnException wrapper has already returned on the caller's thread.
            exchange.Exception = ex;
            try
            {
                await _context.HandleException(exchange, ex, ct).ConfigureAwait(false);
            }
            catch (Exception handlerEx)
            {
                _logger?.LogError(handlerEx,
                    "Threads pool: exception handler failed for exchange {ExchangeId}.", exchange.ExchangeId);
            }
        }
        finally
        {
            Interlocked.Increment(ref _processedCount);
            await exchange.DisposeAsync().ConfigureAwait(false);
        }
    }

    // ── IRouteLifecycleListener — graceful drain on stop ─────────────────────────

    /// <inheritdoc />
    public async Task OnContextStopping(IRouteContext context, CancellationToken ct)
    {
        if (Volatile.Read(ref _started) == 0) return; // never processed anything

        _shuttingDown = true;
        _queue.Writer.TryComplete(); // stop accepting; workers finish the backlog then exit their loop

        var workers = _workers;
        if (workers == null) return;

        var all = Task.WhenAll(workers);
        using var drainCts = new CancellationTokenSource();
        var timer = Task.Delay(DrainTimeout, drainCts.Token);

        if (await Task.WhenAny(all, timer).ConfigureAwait(false) == all)
        {
            drainCts.Cancel(); // drained in time — cancel the timer
        }
        else
        {
            _logger?.LogWarning(
                "Threads pool drain exceeded {Timeout}; force-cancelling {N} worker(s).", DrainTimeout, _poolSize);
            await _workerCts.CancelAsync().ConfigureAwait(false);
            try { await all.ConfigureAwait(false); } catch { /* observed on shutdown */ }
        }

        _workerCts.Dispose();
    }
}

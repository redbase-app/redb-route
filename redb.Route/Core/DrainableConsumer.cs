using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;

namespace redb.Route.Core;

/// <summary>
/// Base class for consumers with Two-CTS graceful shutdown.
/// Handles: CTS lifecycle, inflight tracking, drain-on-stop, force-cancel.
/// Subclasses only implement <see cref="RunAsync"/> and optionally override hooks.
/// </summary>
public abstract class DrainableConsumer : IConsumer
{
    private CancellationTokenSource? _pollCts;
    private readonly InflightDrainGuard _drain = new();
    private Task? _runTask;
    private ILogger? _logger;

    /// <summary>The endpoint this consumer is attached to.</summary>
    protected abstract IEndpoint ConsumerEndpoint { get; }

    /// <summary>The processor to execute on each exchange.</summary>
    protected IProcessor Processor { get; }

    /// <inheritdoc />
    public IEndpoint Endpoint => ConsumerEndpoint;

    /// <summary>Logger resolved from the component.</summary>
    protected ILogger? Logger => _logger ??= (ConsumerEndpoint.Component as ComponentBase)?.Logger;

    /// <summary>Drain timeout for graceful stop.</summary>
    internal TimeSpan DrainTimeout { get => _drain.DrainTimeout; set => _drain.DrainTimeout = value; }

    /// <summary>Display name for logging (e.g. "kafka:orders", "file:/tmp/in").</summary>
    protected abstract string ConsumerName { get; }

    /// <summary>Creates a drainable consumer.</summary>
    protected DrainableConsumer(IProcessor processor)
    {
        Processor = processor ?? throw new ArgumentNullException(nameof(processor));
    }

    // ── IConsumer lifecycle ──

    /// <inheritdoc />
    public async Task Start(CancellationToken ct = default)
    {
        _pollCts?.Dispose();
        _pollCts = new CancellationTokenSource();
        _drain.Start(ct);

        try
        {
            await OnStarting(ct).ConfigureAwait(false);
        }
        catch
        {
            _pollCts.Dispose();
            _pollCts = null;
            _drain.Dispose();
            throw;
        }

        _runTask = RunAsync(_pollCts.Token, _drain.ProcessingToken);

        Logger?.LogInformation("{Consumer} started.", ConsumerName);
    }

    /// <inheritdoc />
    public async Task Stop(CancellationToken ct = default)
    {
        if (_pollCts != null)
        {
            try
            {
                // Step 1: stop accepting new work
                await OnStopAccepting().ConfigureAwait(false);
                await _pollCts.CancelAsync().ConfigureAwait(false);

                // Step 2: drain in-flight
                await _drain.DrainAsync(ct, Logger, ConsumerName).ConfigureAwait(false);

                // Step 3: await run task(s)
                await AwaitRunTask().ConfigureAwait(false);
            }
            finally
            {
                // Step 4: cleanup — guaranteed even if drain/await throws
                _pollCts.Dispose();
                _pollCts = null;
                _drain.Dispose();
            }

            await OnStopped().ConfigureAwait(false);
        }

        Logger?.LogInformation("{Consumer} stopped.", ConsumerName);
    }

    // ── Abstract / virtual hooks ──

    /// <summary>
    /// Main consumer loop. Receives <paramref name="pollCt"/> (stop accepting new work)
    /// and <paramref name="processingCt"/> (cancel in-flight processing).
    /// Use <see cref="ProcessWithTracking"/> for automatic inflight tracking.
    /// </summary>
    protected abstract Task RunAsync(CancellationToken pollCt, CancellationToken processingCt);

    /// <summary>Called during Start() before RunAsync. Override for async setup (topology, subscriptions).</summary>
    protected virtual Task OnStarting(CancellationToken ct) => Task.CompletedTask;

    /// <summary>Called at the beginning of Stop() before poll cancel. Override for protocol-level unsubscribe.</summary>
    protected virtual Task OnStopAccepting() => Task.CompletedTask;

    /// <summary>Called at the end of Stop() after CTS disposal. Override for resource cleanup.</summary>
    protected virtual Task OnStopped() => Task.CompletedTask;

    /// <summary>
    /// Awaits the run task. Override for consumers with multiple workers (e.g. Task.WhenAll).
    /// </summary>
    protected virtual async Task AwaitRunTask()
    {
        if (_runTask != null)
        {
            try { await _runTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
    }

    // ── Inflight tracking helper ──

    /// <summary>
    /// Processes an exchange with automatic inflight count tracking.
    /// Increments before, decrements in finally. Disposes the exchange after processing.
    /// </summary>
    protected async Task ProcessWithTracking(IExchange exchange, CancellationToken processingCt)
    {
        _drain.Increment();
        try
        {
            await Processor.Process(exchange, processingCt).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (processingCt.IsCancellationRequested)
        {
            throw; // shutdown — let the worker loop exit
        }
        catch (Exception ex)
        {
            // Last-resort net: the route's own error handling (OnException / DeadLetterChannel) runs inside the
            // pipeline; only a genuinely unhandled exchange reaches here. Log and drop it — a single failure
            // must NOT terminate the worker and silently stop the consumer draining the queue.
            Logger?.LogError(ex, "{Consumer} dropped an exchange after an unhandled failure; continuing.", ConsumerName);
        }
        finally
        {
            _drain.Decrement();
            await exchange.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Increments the inflight counter. Must be paired with <see cref="DecrementInflight"/>.
    /// Use when you need custom logic between increment and process (e.g. ack/nack).
    /// </summary>
    protected void IncrementInflight() => _drain.Increment();

    /// <summary>Decrements the inflight counter.</summary>
    protected void DecrementInflight() => _drain.Decrement();
}

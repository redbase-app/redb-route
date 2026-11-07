using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;

namespace redb.Route.Core;

/// <summary>
/// Base class for connection-based producers.
/// Handles: started guard, logger resolution, Start/Stop lifecycle.
/// Subclasses implement <see cref="ConnectAsync"/> and optionally override <see cref="DisconnectAsync"/>.
/// </summary>
public abstract class ConnectableProducer : IProducer
{
    private int _started; // 0 = stopped, 1 = fully started (ConnectAsync completed)
    private readonly object _startLock = new();
    private Task? _startTask;
    private ILogger? _logger;

    /// <summary>The endpoint this producer sends to.</summary>
    protected abstract IEndpoint ProducerEndpoint { get; }

    /// <summary>Display name for logging (e.g. "http:https://api.example.com", "kafka:orders").</summary>
    protected abstract string ProducerName { get; }

    /// <summary>Logger resolved from the component.</summary>
    protected ILogger? Logger => _logger ??= (ProducerEndpoint.Component as ComponentBase)?.Logger;

    /// <summary>Whether the producer has been started.</summary>
    public bool IsStarted => Volatile.Read(ref _started) == 1;

    // ── IProducer lifecycle ──

    /// <inheritdoc />
    public Task Start(CancellationToken ct = default)
    {
        // Single-flight: the first caller creates the start task; concurrent callers await the SAME
        // task rather than each starting their own. Crucially, IsStarted flips to true only AFTER
        // ConnectAsync completes (see StartCoreAsync), so a racing Process()/EnsureStarted() can never
        // observe a half-initialised producer (e.g. a Redis producer whose _db is not yet assigned).
        lock (_startLock)
        {
            return _startTask ??= StartCoreAsync(ct);
        }
    }

    private async Task StartCoreAsync(CancellationToken ct)
    {
        try
        {
            await ConnectAsync(ct).ConfigureAwait(false);
            Volatile.Write(ref _started, 1); // observable as started ONLY now that resources are ready
            Logger?.LogInformation("{Producer} started.", ProducerName);
        }
        catch
        {
            // Let a later Start() retry a failed cold start instead of caching the failure forever.
            lock (_startLock) { _startTask = null; }
            throw;
        }
    }

    /// <inheritdoc />
    public async Task Stop(CancellationToken ct = default)
    {
        Task? startInFlight;
        lock (_startLock)
        {
            startInFlight = _startTask;
            _startTask = null;
        }
        if (startInFlight is null) return; // never started

        // Let a concurrent start settle before tearing down (ignore its failure — nothing connected).
        try { await startInFlight.ConfigureAwait(false); } catch { /* start failed */ }

        if (Interlocked.CompareExchange(ref _started, 0, 1) != 1) return;
        await DisconnectAsync(ct).ConfigureAwait(false);
        Logger?.LogInformation("{Producer} stopped.", ProducerName);
    }

    // ── Abstract / virtual hooks ──

    /// <summary>
    /// Set up connection/resources. Awaited by Start() to completion <b>before</b> IsStarted flips to
    /// true, so subclasses may assign their fields here knowing no Process() can run until it returns.
    /// </summary>
    protected abstract Task ConnectAsync(CancellationToken ct);

    /// <summary>Tear down connection/resources. Called from Stop() after the started flag is cleared.</summary>
    protected virtual Task DisconnectAsync(CancellationToken ct) => Task.CompletedTask;

    /// <inheritdoc />
    public abstract Task Process(IExchange exchange, CancellationToken ct = default);

    /// <summary>Throws <see cref="InvalidOperationException"/> if the producer has not been started.</summary>
    protected void EnsureStarted()
    {
        if (!IsStarted)
            throw new InvalidOperationException($"{ProducerName} has not been started. Call Start() first.");
    }
}

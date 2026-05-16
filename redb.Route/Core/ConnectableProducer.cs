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
    private int _started; // 0 = stopped, 1 = started
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
    public async Task Start(CancellationToken ct = default)
    {
        if (Interlocked.CompareExchange(ref _started, 1, 0) != 0) return;
        try
        {
            await ConnectAsync(ct).ConfigureAwait(false);
            Logger?.LogInformation("{Producer} started.", ProducerName);
        }
        catch
        {
            Volatile.Write(ref _started, 0);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task Stop(CancellationToken ct = default)
    {
        if (Interlocked.CompareExchange(ref _started, 0, 1) != 1) return;
        await DisconnectAsync(ct).ConfigureAwait(false);
        Logger?.LogInformation("{Producer} stopped.", ProducerName);
    }

    // ── Abstract / virtual hooks ──

    /// <summary>Set up connection/resources. Called from Start() before the started flag is set.</summary>
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

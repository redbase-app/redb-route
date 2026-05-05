using System.Collections.Concurrent;
using redb.Route.Abstractions;
using redb.Route.Components;

namespace redb.Route.Core;

/// <summary>
/// Template for polling messages from endpoints programmatically.
/// Supports optimized polling from SEDA/channel-based endpoints and generic
/// consumer-based polling for all other endpoint types.
/// </summary>
public class ConsumerTemplate : IConsumerTemplate, IDisposable
{
    private volatile bool _started;
    private volatile bool _disposed;

    /// <summary>
    /// Initializes a new instance with the specified route context.
    /// </summary>
    /// <param name="context">The route context for endpoint resolution.</param>
    public ConsumerTemplate(IRouteContext context)
    {
        Context = context ?? throw new ArgumentNullException(nameof(context));
    }

    /// <inheritdoc />
    public IRouteContext Context { get; }

    /// <inheritdoc />
    public bool IsStarted => _started;

    // ── Receive (blocking) ──

    /// <inheritdoc />
    public Task<IExchange> Receive(string endpointUri, CancellationToken ct = default)
    {
        EnsureStarted();
        var endpoint = Context.GetEndpoint(endpointUri);
        return Receive(endpoint, ct);
    }

    /// <inheritdoc />
    public async Task<IExchange> Receive(IEndpoint endpoint, CancellationToken ct = default)
    {
        EnsureStarted();
        ArgumentNullException.ThrowIfNull(endpoint);

        // Optimized path for SEDA — read directly from the channel
        if (endpoint is SedaEndpoint seda)
            return await seda.Queue.Reader.ReadAsync(ct).ConfigureAwait(false);

        // Generic path — create a temporary consumer
        return await ReceiveViaConsumer(endpoint, Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false)
               ?? throw new OperationCanceledException("Receive was cancelled.", ct);
    }

    // ── Receive with timeout ──

    /// <inheritdoc />
    public Task<IExchange?> Receive(string endpointUri, TimeSpan timeout, CancellationToken ct = default)
    {
        EnsureStarted();
        var endpoint = Context.GetEndpoint(endpointUri);
        return Receive(endpoint, timeout, ct);
    }

    /// <inheritdoc />
    public async Task<IExchange?> Receive(IEndpoint endpoint, TimeSpan timeout, CancellationToken ct = default)
    {
        EnsureStarted();
        ArgumentNullException.ThrowIfNull(endpoint);

        // Optimized path for SEDA
        if (endpoint is SedaEndpoint seda)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            try
            {
                return await seda.Queue.Reader.ReadAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return null; // Timeout — not caller cancellation
            }
        }

        // Generic path
        return await ReceiveViaConsumer(endpoint, timeout, ct).ConfigureAwait(false);
    }

    // ── ReceiveNoWait ──

    /// <inheritdoc />
    public Task<IExchange?> ReceiveNoWait(string endpointUri, CancellationToken ct = default)
    {
        EnsureStarted();
        var endpoint = Context.GetEndpoint(endpointUri);
        return ReceiveNoWait(endpoint, ct);
    }

    /// <inheritdoc />
    public Task<IExchange?> ReceiveNoWait(IEndpoint endpoint, CancellationToken ct = default)
    {
        EnsureStarted();
        ArgumentNullException.ThrowIfNull(endpoint);
        ct.ThrowIfCancellationRequested();

        // Optimized path for SEDA
        if (endpoint is SedaEndpoint seda)
        {
            return Task.FromResult<IExchange?>(
                seda.Queue.Reader.TryRead(out var exchange) ? exchange : null);
        }

        // Generic: try with zero timeout
        return Receive(endpoint, TimeSpan.Zero, ct);
    }

    // ── Typed body convenience ──

    /// <inheritdoc />
    public async Task<object?> ReceiveBody(string endpointUri, CancellationToken ct = default)
    {
        var exchange = await Receive(endpointUri, ct).ConfigureAwait(false);
        return exchange.In.Body;
    }

    /// <inheritdoc />
    public async Task<T?> ReceiveBody<T>(string endpointUri, CancellationToken ct = default)
    {
        var exchange = await Receive(endpointUri, ct).ConfigureAwait(false);
        return ConvertBody<T>(exchange.In.Body);
    }

    /// <inheritdoc />
    public async Task<T?> ReceiveBody<T>(string endpointUri, TimeSpan timeout, CancellationToken ct = default)
    {
        var exchange = await Receive(endpointUri, timeout, ct).ConfigureAwait(false);
        return exchange is null ? default : ConvertBody<T>(exchange.In.Body);
    }

    // ── Lifecycle ──

    /// <inheritdoc />
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started)
            throw new InvalidOperationException("ConsumerTemplate is already started.");
        _started = true;
    }

    /// <inheritdoc />
    public void Stop()
    {
        if (!_started)
            throw new InvalidOperationException("ConsumerTemplate is not started.");
        _started = false;
    }

    /// <summary>Disposes the consumer template and releases resources.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _started = false;
        GC.SuppressFinalize(this);
    }

    // ── Private helpers ──

    /// <summary>
    /// Generic receive path: creates a temporary consumer with a bridge processor
    /// that captures the first exchange into a TaskCompletionSource.
    /// </summary>
    private async Task<IExchange?> ReceiveViaConsumer(IEndpoint endpoint, TimeSpan timeout, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<IExchange>(TaskCreationOptions.RunContinuationsAsynchronously);
        var bridge = new BridgeProcessor(tcs);
        var consumer = endpoint.CreateConsumer(bridge);

        try
        {
            await consumer.Start(ct).ConfigureAwait(false);

            if (timeout == Timeout.InfiniteTimeSpan)
            {
                // Wait indefinitely (respecting cancellation)
                using var reg = ct.Register(() => tcs.TrySetCanceled(ct));
                return await tcs.Task.ConfigureAwait(false);
            }
            else
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(timeout);
                using var reg = cts.Token.Register(() => tcs.TrySetCanceled(cts.Token));
                try
                {
                    return await tcs.Task.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    return null; // Timeout
                }
            }
        }
        finally
        {
            await consumer.Stop(CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static T? ConvertBody<T>(object? body)
    {
        if (body is null) return default;
        if (body is T typed) return typed;
        try
        {
            return (T)Convert.ChangeType(body, typeof(T));
        }
        catch
        {
            return default;
        }
    }

    private void EnsureStarted()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_started)
            throw new InvalidOperationException(
                "ConsumerTemplate is not started. Call Start() before use.");
    }

    /// <summary>
    /// Internal processor that captures the first exchange into a TaskCompletionSource.
    /// </summary>
    private sealed class BridgeProcessor : IProcessor
    {
        private readonly TaskCompletionSource<IExchange> _tcs;

        public BridgeProcessor(TaskCompletionSource<IExchange> tcs) => _tcs = tcs;

        public Task Process(IExchange exchange, CancellationToken ct = default)
        {
            _tcs.TrySetResult(exchange);
            return Task.CompletedTask;
        }
    }
}

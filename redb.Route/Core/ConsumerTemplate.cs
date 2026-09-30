using System.Collections.Concurrent;
using System.Threading.Channels;
using redb.Route.Abstractions;
using redb.Route.Components;

namespace redb.Route.Core;

/// <summary>
/// Template for polling messages from endpoints programmatically, as Apache Camel's.
/// SEDA endpoints are read straight from their queue. Any other endpoint gets one consumer per
/// endpoint, started on first use and kept until <see cref="Stop"/>: every exchange it produces waits
/// in a queue for a <c>Receive</c>, and its source does not commit it (delete or move the file,
/// acknowledge the message) until the caller completes it with <see cref="DoneUoW"/>. Exchanges no
/// <c>Receive</c> took stay uncommitted in their source.
/// </summary>
public class ConsumerTemplate : IConsumerTemplate, IDisposable
{
    private readonly ConcurrentDictionary<IEndpoint, Lazy<Task<CachedConsumer>>> _consumers =
        new(ReferenceEqualityComparer.Instance);

    // Exchanges handed out and not completed yet; the value is null for a SEDA exchange, which has
    // no unit of work to complete.
    private readonly ConcurrentDictionary<IExchange, Offer?> _open = new(ReferenceEqualityComparer.Instance);

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
            return HandOut(await seda.Queue.Reader.ReadAsync(ct).ConfigureAwait(false), null);

        return await ReceiveViaConsumer(endpoint, Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false)
               ?? throw new OperationCanceledException("Receive was cancelled: the template stopped.", ct);
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
                return HandOut(await seda.Queue.Reader.ReadAsync(cts.Token).ConfigureAwait(false), null);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return null; // Timeout — not caller cancellation
            }
        }

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
                seda.Queue.Reader.TryRead(out var exchange) ? HandOut(exchange, null) : null);
        }

        // Generic: whatever the endpoint's consumer has already offered
        return Receive(endpoint, TimeSpan.Zero, ct);
    }

    // ── Typed body convenience ──

    /// <inheritdoc />
    public async Task<object?> ReceiveBody(string endpointUri, CancellationToken ct = default)
    {
        var exchange = await Receive(endpointUri, ct).ConfigureAwait(false);
        return await TakeBodyAndComplete(exchange).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<T?> ReceiveBody<T>(string endpointUri, CancellationToken ct = default)
    {
        var exchange = await Receive(endpointUri, ct).ConfigureAwait(false);
        return ConvertBody<T>(await TakeBodyAndComplete(exchange).ConfigureAwait(false));
    }

    /// <inheritdoc />
    public async Task<T?> ReceiveBody<T>(string endpointUri, TimeSpan timeout, CancellationToken ct = default)
    {
        var exchange = await Receive(endpointUri, timeout, ct).ConfigureAwait(false);
        return exchange is null ? default : ConvertBody<T>(await TakeBodyAndComplete(exchange).ConfigureAwait(false));
    }

    // ── Unit of work ──

    /// <inheritdoc />
    public Task DoneUoW(IExchange exchange)
    {
        ArgumentNullException.ThrowIfNull(exchange);

        if (!_open.TryRemove(exchange, out var offer))
            throw new InvalidOperationException(
                "The exchange is not an open unit of work of this ConsumerTemplate: it was not handed out by it, " +
                "was already completed, or was rolled back when the template stopped.");

        // SEDA: nothing waits for the caller.
        if (offer is null)
            return Task.CompletedTask;

        if (!offer.Done.TrySetResult())
            throw new InvalidOperationException(
                "The exchange was rolled back before it was completed: its consumer stopped.");
        return Task.CompletedTask;
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
        StopConsumersAsync().GetAwaiter().GetResult();
    }

    /// <summary>Disposes the consumer template, stopping its consumers.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_started)
        {
            _started = false;
            StopConsumersAsync().GetAwaiter().GetResult();
        }
        GC.SuppressFinalize(this);
    }

    // ── Private helpers ──

    private IExchange HandOut(IExchange exchange, Offer? offer)
    {
        _open[exchange] = offer;
        return exchange;
    }

    /// <summary>
    /// Takes the next exchange the endpoint's consumer offers. The consumer is started on first use
    /// and kept; an offer abandoned by a stopping consumer is skipped.
    /// </summary>
    private async Task<IExchange?> ReceiveViaConsumer(IEndpoint endpoint, TimeSpan timeout, CancellationToken ct)
    {
        var cached = await GetOrStart(endpoint, ct).ConfigureAwait(false);
        var reader = cached.Queue.Reader;

        while (reader.TryRead(out var ready))
            if (!ready.Done.Task.IsCompleted)
                return HandOut(ready.Exchange, ready);
        if (timeout == TimeSpan.Zero)
            return null;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct, cached.Stopping.Token);
        if (timeout != Timeout.InfiniteTimeSpan)
            cts.CancelAfter(timeout);
        try
        {
            while (true)
            {
                var offer = await reader.ReadAsync(cts.Token).ConfigureAwait(false);
                if (!offer.Done.Task.IsCompleted)
                    return HandOut(offer.Exchange, offer);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null; // Timeout, or the template stopped — not caller cancellation
        }
    }

    private async Task<CachedConsumer> GetOrStart(IEndpoint endpoint, CancellationToken ct)
    {
        var lazy = _consumers.GetOrAdd(endpoint,
            e => new Lazy<Task<CachedConsumer>>(() => StartConsumer(e)));
        try
        {
            return await lazy.Value.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (Exception) when (lazy.Value.IsFaulted)
        {
            // A consumer that failed to start is not cached: the next Receive tries again.
            _consumers.TryRemove(new KeyValuePair<IEndpoint, Lazy<Task<CachedConsumer>>>(endpoint, lazy));
            throw;
        }
    }

    private static async Task<CachedConsumer> StartConsumer(IEndpoint endpoint)
    {
        var cached = new CachedConsumer();
        cached.Consumer = endpoint.CreateConsumer(new BridgeProcessor(cached));
        await cached.Consumer.Start(CancellationToken.None).ConfigureAwait(false);
        return cached;
    }

    private async Task StopConsumersAsync()
    {
        _open.Clear();
        var consumers = _consumers.Values.ToList();
        _consumers.Clear();

        foreach (var lazy in consumers)
        {
            if (!lazy.IsValueCreated || !lazy.Value.IsCompletedSuccessfully)
                continue;
            var cached = lazy.Value.Result;
            if (cached.Consumer is DrainableConsumer drainable)
            {
                // Every exchange in flight here waits for a caller, and none will come: no drain,
                // straight to cancelling the processing token, so the consumer rolls them back on
                // its own shutdown path.
                drainable.DrainTimeout = TimeSpan.Zero;
                await cached.Consumer.Stop(CancellationToken.None).ConfigureAwait(false);
                await cached.Stopping.CancelAsync().ConfigureAwait(false);
            }
            else
            {
                // A consumer without a processing token may wait for its handlers while stopping:
                // release them once the stop has begun, so no new work is accepted in between.
                var stop = cached.Consumer!.Stop(CancellationToken.None);
                await cached.Stopping.CancelAsync().ConfigureAwait(false);
                await stop.ConfigureAwait(false);
            }
            cached.Stopping.Dispose();
        }
    }

    /// <summary>
    /// ReceiveBody hands the exchange to no one, so it completes the unit of work itself once the
    /// body is taken — or rolls it back when the body cannot be read.
    /// </summary>
    private async Task<object?> TakeBodyAndComplete(IExchange exchange)
    {
        object? body;
        try
        {
            body = exchange.In.Body;
            // The source releases the exchange's resources once the unit of work is done, and a
            // streamed body goes with them: it is read into memory first.
            if (body is Stream stream)
            {
                var copy = new MemoryStream();
                await stream.CopyToAsync(copy).ConfigureAwait(false);
                copy.Position = 0;
                body = copy;
            }
        }
        catch (Exception ex)
        {
            exchange.Exception ??= ex;
            await DoneUoW(exchange).ConfigureAwait(false);
            throw;
        }
        await DoneUoW(exchange).ConfigureAwait(false);
        return body;
    }

    private static T? ConvertBody<T>(object? body) => TypedValue.Convert<T>(body, "received body");

    private void EnsureStarted()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_started)
            throw new InvalidOperationException(
                "ConsumerTemplate is not started. Call Start() before use.");
    }

    /// <summary>An exchange a consumer offered, and the signal that releases the consumer.</summary>
    private sealed record Offer(IExchange Exchange)
    {
        public TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>One endpoint's consumer, its queue of offers, and the token that abandons them.</summary>
    private sealed class CachedConsumer
    {
        public IConsumer? Consumer { get; set; }
        public Channel<Offer> Queue { get; } = Channel.CreateUnbounded<Offer>();
        public CancellationTokenSource Stopping { get; } = new();
    }

    /// <summary>
    /// Offers each exchange to the template and holds the consumer until the caller completes it, so
    /// the source commits only what a caller finished. Cancelled by the consumer's processing token
    /// or by the template stopping, it throws <see cref="OperationCanceledException"/> and the
    /// consumer rolls the exchange back.
    /// </summary>
    private sealed class BridgeProcessor(CachedConsumer cached) : IProcessor
    {
        public async Task Process(IExchange exchange, CancellationToken ct = default)
        {
            var offer = new Offer(exchange);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, cached.Stopping.Token);
            await using var registration = linked.Token.Register(() => offer.Done.TrySetCanceled(linked.Token));
            await cached.Queue.Writer.WriteAsync(offer, linked.Token).ConfigureAwait(false);
            await offer.Done.Task.ConfigureAwait(false);
        }
    }
}

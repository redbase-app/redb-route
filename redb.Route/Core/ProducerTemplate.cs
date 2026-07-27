using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using redb.Route.Abstractions;

namespace redb.Route.Core;

/// <summary>
/// Default implementation of <see cref="IProducerTemplate"/>.
/// Provides a client API for sending messages to endpoints outside of route pipelines.
/// Must be started via <see cref="Start"/> before use.
/// Thread-safe: all public methods check started state, endpoint resolution is delegated
/// to <see cref="IRouteContext"/> which uses concurrent collections.
/// Caches producers per endpoint for performance (avoids CreateProducer on every call).
/// </summary>
public class ProducerTemplate : IProducerTemplate, IDisposable
{
    private readonly ConcurrentDictionary<string, IProducer> _producerCache = new(StringComparer.OrdinalIgnoreCase);
    private volatile bool _started;
    private volatile bool _disposed;
    // Captured once at construction; null when context has no DI provider (e.g. unit-test routes).
    // When non-null, every Exchange we create owns a fresh DI scope so per-request scoped
    // services (IRedbService and its NpgsqlConnection, OpenIddict stores, ...) are isolated
    // between concurrent ProducerTemplate calls. Each Send/RequestBody method below MUST
    // dispose the exchange in finally so the scope (and connection) is released promptly.
    private readonly IServiceScopeFactory? _scopeFactory;

    /// <summary>
    /// Initializes a new instance with the specified route context.
    /// </summary>
    /// <param name="context">The route context for endpoint resolution.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="context"/> is <c>null</c>.</exception>
    public ProducerTemplate(IRouteContext context)
    {
        Context = context ?? throw new ArgumentNullException(nameof(context));
        _scopeFactory = context.GetServiceProvider()?.GetService<IServiceScopeFactory>();
    }

    /// <inheritdoc />
    public IRouteContext Context { get; }

    /// <inheritdoc />
    public bool IsStarted => _started;

    // ── Synchronous Send ──

    /// <inheritdoc />
    public void Send(string endpointUri, object body)
    {
        EnsureStarted();
        var endpoint = Context.GetEndpoint(endpointUri);
        Send(endpoint, body);
    }

    /// <inheritdoc />
    public void Send(IEndpoint endpoint, object body)
    {
        EnsureStarted();
        var exchange = CreateExchange(body);
        try { Send(endpoint, exchange); }
        finally { exchange.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    }

    /// <inheritdoc />
    public void Send(IEndpoint endpoint, IExchange exchange)
    {
        EnsureStarted();
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(exchange);

        var producer = GetOrCreateProducer(endpoint);
        producer.Process(exchange).GetAwaiter().GetResult();
    }

    /// <inheritdoc />
    public void Send(string endpointUri, IExchange exchange)
    {
        EnsureStarted();
        var endpoint = Context.GetEndpoint(endpointUri);
        Send(endpoint, exchange);
    }

    /// <inheritdoc />
    public void Send(IEndpoint endpoint, IMessage message)
    {
        EnsureStarted();
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(message);

        var exchange = Exchange.Create(message, _scopeFactory);
        try { Send(endpoint, exchange); }
        finally { exchange.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    }

    /// <inheritdoc />
    public void Send(string endpointUri, IMessage message)
    {
        EnsureStarted();
        var endpoint = Context.GetEndpoint(endpointUri);
        Send(endpoint, message);
    }

    /// <inheritdoc />
    public void Send(IEndpoint endpoint, IProcessor processor)
    {
        EnsureStarted();
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(processor);

        var exchange = CreateExchange(null);
        try
        {
            processor.Process(exchange).GetAwaiter().GetResult();
            var producer = GetOrCreateProducer(endpoint);
            producer.Process(exchange).GetAwaiter().GetResult();
        }
        finally { exchange.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    }

    // ── Async Send ──

    /// <inheritdoc />
    public async Task SendAsync(IEndpoint endpoint, object body)
    {
        EnsureStarted();
        ArgumentNullException.ThrowIfNull(endpoint);

        var exchange = CreateExchange(body);
        try
        {
            var producer = GetOrCreateProducer(endpoint);
            // ConnectableProducer-based transports demand Start() before Process();
            // idempotent — the started flag short-circuits subsequent calls.
            await producer.Start().ConfigureAwait(false);
            await producer.Process(exchange).ConfigureAwait(false);
        }
        finally { await exchange.DisposeAsync().ConfigureAwait(false); }
    }

    /// <inheritdoc />
    public async Task SendAsync(string endpointUri, object body)
    {
        EnsureStarted();
        var endpoint = Context.GetEndpoint(endpointUri);
        await SendAsync(endpoint, body).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task SendAsync(IEndpoint endpoint, IMessage message)
    {
        EnsureStarted();
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(message);

        var exchange = Exchange.Create(message, _scopeFactory);
        try
        {
            var producer = GetOrCreateProducer(endpoint);
            // ConnectableProducer-based transports (http, kafka, amqp, …)
            // demand Start() before Process(); idempotent — second Start is
            // a no-op on the started flag.
            await producer.Start().ConfigureAwait(false);
            await producer.Process(exchange).ConfigureAwait(false);
        }
        finally { await exchange.DisposeAsync().ConfigureAwait(false); }
    }

    /// <inheritdoc />
    public async Task SendAsync(string endpointUri, IMessage message)
    {
        EnsureStarted();
        var endpoint = Context.GetEndpoint(endpointUri);
        await SendAsync(endpoint, message).ConfigureAwait(false);
    }

    // ── Async Send / Request of a pre-built exchange (caller-owned) ──

    /// <inheritdoc />
    public async Task SendAsync(IEndpoint endpoint, IExchange exchange, CancellationToken cancellationToken = default)
    {
        EnsureStarted();
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(exchange);

        // Caller-owned exchange: unlike the body/message overloads we do NOT create, clone or
        // dispose it, and we attach no DI scope — whatever scope the exchange carries (or lacks)
        // is the caller's responsibility. A snapshot cloned from a completed exchange may reference
        // a disposed scope; the caller must give it a live scope before replay if it needs one.
        var producer = GetOrCreateProducer(endpoint);
        await producer.Start(cancellationToken).ConfigureAwait(false);
        await producer.Process(exchange, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task SendAsync(string endpointUri, IExchange exchange, CancellationToken cancellationToken = default)
    {
        EnsureStarted();
        var endpoint = Context.GetEndpoint(endpointUri);
        await SendAsync(endpoint, exchange, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IExchange> RequestAsync(IEndpoint endpoint, IExchange exchange, CancellationToken cancellationToken = default)
    {
        EnsureStarted();
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(exchange);

        // Caller-owned (see SendAsync(IExchange) above): not cloned or disposed here.
        exchange.Pattern = ExchangePattern.InOut;
        var producer = GetOrCreateProducer(endpoint);
        await producer.Start(cancellationToken).ConfigureAwait(false);
        await producer.Process(exchange, cancellationToken).ConfigureAwait(false);
        return exchange;
    }

    /// <inheritdoc />
    public async Task<IExchange> RequestAsync(string endpointUri, IExchange exchange, CancellationToken cancellationToken = default)
    {
        EnsureStarted();
        var endpoint = Context.GetEndpoint(endpointUri);
        return await RequestAsync(endpoint, exchange, cancellationToken).ConfigureAwait(false);
    }

    // ── Request/Reply ──

    /// <inheritdoc />
    public async Task<object?> RequestBody(IEndpoint endpoint, object body, CancellationToken cancellationToken = default)
    {
        EnsureStarted();
        ArgumentNullException.ThrowIfNull(endpoint);

        var exchange = CreateExchange(body);
        try
        {
            exchange.Pattern = ExchangePattern.InOut;
            var producer = GetOrCreateProducer(endpoint);
            // ConnectableProducer-based transports demand Start() before Process().
            await producer.Start(cancellationToken).ConfigureAwait(false);
            await producer.Process(exchange, cancellationToken).ConfigureAwait(false);
            return exchange.Out?.Body ?? exchange.In.Body;
        }
        finally { await exchange.DisposeAsync().ConfigureAwait(false); }
    }

    /// <inheritdoc />
    public async Task<object?> RequestBody(string endpointUri, object body, CancellationToken cancellationToken = default)
    {
        EnsureStarted();
        var endpoint = Context.GetEndpoint(endpointUri);
        return await RequestBody(endpoint, body, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<object?> RequestBody(IEndpoint endpoint, IMessage message, CancellationToken cancellationToken = default)
    {
        EnsureStarted();
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(message);

        var exchange = Exchange.Create(message, _scopeFactory);
        try
        {
            exchange.Pattern = ExchangePattern.InOut;
            var producer = GetOrCreateProducer(endpoint);
            // ConnectableProducer-based transports demand Start() before Process().
            await producer.Start(cancellationToken).ConfigureAwait(false);
            await producer.Process(exchange, cancellationToken).ConfigureAwait(false);
            return exchange.Out?.Body ?? exchange.In.Body;
        }
        finally { await exchange.DisposeAsync().ConfigureAwait(false); }
    }

    /// <inheritdoc />
    public async Task<object?> RequestBody(string endpointUri, IMessage message, CancellationToken cancellationToken = default)
    {
        EnsureStarted();
        var endpoint = Context.GetEndpoint(endpointUri);
        return await RequestBody(endpoint, message, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<T?> RequestBody<T>(IEndpoint endpoint, object body, CancellationToken cancellationToken = default)
    {
        var result = await RequestBody(endpoint, body, cancellationToken).ConfigureAwait(false);
        if (result is null)
            return default;
        if (result is T typed)
            return typed;

        try
        {
            return (T)Convert.ChangeType(result, typeof(T));
        }
        catch
        {
            return default;
        }
    }

    /// <inheritdoc />
    public async Task<T?> RequestBody<T>(string endpointUri, object body, CancellationToken cancellationToken = default)
    {
        EnsureStarted();
        var endpoint = Context.GetEndpoint(endpointUri);
        return await RequestBody<T>(endpoint, body, cancellationToken).ConfigureAwait(false);
    }

    // ── Lifecycle ──

    /// <inheritdoc />
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started)
            throw new InvalidOperationException("ProducerTemplate is already started.");
        _started = true;
    }

    /// <inheritdoc />
    public void Stop()
    {
        if (!_started)
            throw new InvalidOperationException("ProducerTemplate is not started.");
        _started = false;
    }

    /// <summary>Disposes cached producers and releases resources.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _started = false;
        _producerCache.Clear();
        GC.SuppressFinalize(this);
    }

    // ── Private helpers ──

    private IProducer GetOrCreateProducer(IEndpoint endpoint)
    {
        var key = endpoint.Uri.NormalizedKey;
        return _producerCache.GetOrAdd(key, _ => endpoint.CreateProducer());
    }

    private Exchange CreateExchange(object? body)
    {
        var message = new Message();
        if (body is not null)
            message.Body = body;
        return Exchange.Create(message, _scopeFactory);
    }

    private void EnsureStarted()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_started)
            throw new InvalidOperationException(
                "ProducerTemplate is not started. Call Start() before use.");
    }
}

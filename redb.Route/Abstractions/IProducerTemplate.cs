namespace redb.Route.Abstractions;

/// <summary>
/// Client API for sending messages to endpoints programmatically, outside of route pipelines.
/// Must be started before use via <see cref="Start"/>. Supports fire-and-forget, request/reply,
/// and processor-based send patterns. Thread-safe after <see cref="Start"/> is called.
/// </summary>
public interface IProducerTemplate
{
    /// <summary>Gets the route context associated with this template.</summary>
    IRouteContext Context { get; }

    /// <summary>Gets whether this template has been started and is ready for use.</summary>
    bool IsStarted { get; }

    // ── Synchronous Send ──

    /// <summary>Sends a message body to the specified endpoint URI.</summary>
    /// <param name="endpointUri">Target endpoint URI (e.g., "direct:orders").</param>
    /// <param name="body">Message body to send.</param>
    void Send(string endpointUri, object body);

    /// <summary>Sends a message body to the specified endpoint.</summary>
    /// <param name="endpoint">Target endpoint instance.</param>
    /// <param name="body">Message body to send.</param>
    void Send(IEndpoint endpoint, object body);

    /// <summary>Sends a pre-built exchange to the specified endpoint.</summary>
    /// <param name="endpoint">Target endpoint instance.</param>
    /// <param name="exchange">Exchange to send.</param>
    void Send(IEndpoint endpoint, IExchange exchange);

    /// <summary>Sends a message to the specified endpoint.</summary>
    /// <param name="endpoint">Target endpoint instance.</param>
    /// <param name="message">Message to wrap in an exchange and send.</param>
    void Send(IEndpoint endpoint, IMessage message);

    /// <summary>Sends a message to the endpoint resolved from URI.</summary>
    /// <param name="endpointUri">Target endpoint URI.</param>
    /// <param name="message">Message to send.</param>
    void Send(string endpointUri, IMessage message);

    /// <summary>Applies a processor to an exchange and sends it to the endpoint.</summary>
    /// <param name="endpoint">Target endpoint instance.</param>
    /// <param name="processor">Processor to apply to the exchange before sending.</param>
    void Send(IEndpoint endpoint, IProcessor processor);

    // ── Async Send (fire-and-forget) ──

    /// <summary>Asynchronously sends a message body to the endpoint.</summary>
    /// <param name="endpoint">Target endpoint instance.</param>
    /// <param name="body">Message body to send.</param>
    Task SendAsync(IEndpoint endpoint, object body);

    /// <summary>Asynchronously sends a message body to the endpoint URI.</summary>
    /// <param name="endpointUri">Target endpoint URI.</param>
    /// <param name="body">Message body to send.</param>
    Task SendAsync(string endpointUri, object body);

    /// <summary>Asynchronously sends a message to the endpoint.</summary>
    /// <param name="endpoint">Target endpoint instance.</param>
    /// <param name="message">Message to send.</param>
    Task SendAsync(IEndpoint endpoint, IMessage message);

    /// <summary>Asynchronously sends a message to the endpoint URI.</summary>
    /// <param name="endpointUri">Target endpoint URI.</param>
    /// <param name="message">Message to send.</param>
    Task SendAsync(string endpointUri, IMessage message);

    // ── Request/Reply ──

    /// <summary>
    /// Sends a request and waits for the reply body asynchronously.
    /// Uses <see cref="ExchangePattern.InOut"/> to signal request/reply semantics.
    /// </summary>
    /// <param name="endpoint">Target endpoint instance.</param>
    /// <param name="body">Request body.</param>
    /// <param name="cancellationToken">Token to cancel the in-flight request.</param>
    /// <returns>The response body from <see cref="IExchange.Out"/>, or <c>null</c>.</returns>
    Task<object?> RequestBody(IEndpoint endpoint, object body, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a request by URI and waits for the reply body asynchronously.
    /// </summary>
    /// <param name="endpointUri">Target endpoint URI.</param>
    /// <param name="body">Request body.</param>
    /// <param name="cancellationToken">Token to cancel the in-flight request.</param>
    /// <returns>The response body from <see cref="IExchange.Out"/>, or <c>null</c>.</returns>
    Task<object?> RequestBody(string endpointUri, object body, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends an explicit <see cref="IMessage"/> as the request and waits for the reply body.
    /// Headers and ContentType on the message are preserved (the message is not re-wrapped).
    /// </summary>
    Task<object?> RequestBody(IEndpoint endpoint, IMessage message, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends an explicit <see cref="IMessage"/> by URI and waits for the reply body.
    /// </summary>
    Task<object?> RequestBody(string endpointUri, IMessage message, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a request and returns the reply body cast to <typeparamref name="T"/>.
    /// </summary>
    /// <typeparam name="T">Expected response type.</typeparam>
    /// <param name="endpoint">Target endpoint instance.</param>
    /// <param name="body">Request body.</param>
    /// <param name="cancellationToken">Token to cancel the in-flight request.</param>
    /// <returns>The typed response body, or <c>default</c>.</returns>
    Task<T?> RequestBody<T>(IEndpoint endpoint, object body, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a request by URI and returns the reply body cast to <typeparamref name="T"/>.
    /// </summary>
    /// <typeparam name="T">Expected response type.</typeparam>
    /// <param name="endpointUri">Target endpoint URI.</param>
    /// <param name="body">Request body.</param>
    /// <param name="cancellationToken">Token to cancel the in-flight request.</param>
    /// <returns>The typed response body, or <c>default</c>.</returns>
    Task<T?> RequestBody<T>(string endpointUri, object body, CancellationToken cancellationToken = default);

    // ── Lifecycle ──

    /// <summary>Starts the producer template. Must be called before any send/request methods.</summary>
    void Start();

    /// <summary>Stops the producer template. No send/request methods may be called after this.</summary>
    void Stop();
}

namespace redb.Route.Abstractions;

/// <summary>
/// Template for polling messages from endpoints programmatically,
/// without creating a full route. Mirrors Apache Camel's ConsumerTemplate.
/// <para>
/// Use <see cref="Receive(string, CancellationToken)"/> for blocking poll,
/// <see cref="Receive(string, TimeSpan, CancellationToken)"/> for timed poll,
/// and <see cref="ReceiveNoWait(string, CancellationToken)"/> for non-blocking poll.
/// </para>
/// </summary>
public interface IConsumerTemplate
{
    /// <summary>Gets the route context associated with this template.</summary>
    IRouteContext Context { get; }

    /// <summary>Gets whether this template has been started and is ready for use.</summary>
    bool IsStarted { get; }

    // ── Receive (blocking) ──

    /// <summary>
    /// Receives an exchange from the specified endpoint URI, blocking until one is available.
    /// </summary>
    /// <param name="endpointUri">Source endpoint URI (e.g., "seda:queue").</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The received exchange.</returns>
    Task<IExchange> Receive(string endpointUri, CancellationToken ct = default);

    /// <summary>
    /// Receives an exchange from the specified endpoint, blocking until one is available.
    /// </summary>
    /// <param name="endpoint">Source endpoint instance.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The received exchange.</returns>
    Task<IExchange> Receive(IEndpoint endpoint, CancellationToken ct = default);

    // ── Receive with timeout ──

    /// <summary>
    /// Receives an exchange from the specified endpoint URI, waiting up to the given timeout.
    /// Returns <c>null</c> if no exchange is available within the timeout.
    /// </summary>
    /// <param name="endpointUri">Source endpoint URI.</param>
    /// <param name="timeout">Maximum time to wait.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The received exchange, or <c>null</c> if timed out.</returns>
    Task<IExchange?> Receive(string endpointUri, TimeSpan timeout, CancellationToken ct = default);

    /// <summary>
    /// Receives an exchange from the specified endpoint, waiting up to the given timeout.
    /// Returns <c>null</c> if no exchange is available within the timeout.
    /// </summary>
    /// <param name="endpoint">Source endpoint instance.</param>
    /// <param name="timeout">Maximum time to wait.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The received exchange, or <c>null</c> if timed out.</returns>
    Task<IExchange?> Receive(IEndpoint endpoint, TimeSpan timeout, CancellationToken ct = default);

    // ── ReceiveNoWait (non-blocking) ──

    /// <summary>
    /// Attempts to receive an exchange without waiting.
    /// Returns <c>null</c> immediately if no exchange is available.
    /// </summary>
    /// <param name="endpointUri">Source endpoint URI.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The received exchange, or <c>null</c> if none available.</returns>
    Task<IExchange?> ReceiveNoWait(string endpointUri, CancellationToken ct = default);

    /// <summary>
    /// Attempts to receive an exchange without waiting.
    /// Returns <c>null</c> immediately if no exchange is available.
    /// </summary>
    /// <param name="endpoint">Source endpoint instance.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The received exchange, or <c>null</c> if none available.</returns>
    Task<IExchange?> ReceiveNoWait(IEndpoint endpoint, CancellationToken ct = default);

    // ── Typed body convenience ──

    /// <summary>
    /// Receives the message body from the specified endpoint URI, blocking until available.
    /// </summary>
    /// <param name="endpointUri">Source endpoint URI.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The body of the incoming message.</returns>
    Task<object?> ReceiveBody(string endpointUri, CancellationToken ct = default);

    /// <summary>
    /// Receives the message body from the specified endpoint URI, blocking until available,
    /// and converts it to <typeparamref name="T"/>.
    /// </summary>
    /// <typeparam name="T">Expected body type.</typeparam>
    /// <param name="endpointUri">Source endpoint URI.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The typed body, or <c>default</c>.</returns>
    Task<T?> ReceiveBody<T>(string endpointUri, CancellationToken ct = default);

    /// <summary>
    /// Receives the message body with a timeout. Returns <c>default</c> if timed out.
    /// </summary>
    /// <typeparam name="T">Expected body type.</typeparam>
    /// <param name="endpointUri">Source endpoint URI.</param>
    /// <param name="timeout">Maximum time to wait.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The typed body, or <c>default</c> if timed out or conversion failed.</returns>
    Task<T?> ReceiveBody<T>(string endpointUri, TimeSpan timeout, CancellationToken ct = default);

    // ── Unit of work ──

    /// <summary>
    /// Completes the unit of work of an exchange handed out by a <c>Receive</c> call, Camel's
    /// <c>doneUoW</c>: only now does the source commit it — a polled file is deleted or moved, a
    /// broker message acknowledged. An exchange whose <see cref="IExchange.Exception"/> is set is
    /// rolled back instead (the file stays, the message is redelivered). Read the body before this
    /// call: the source releases the exchange's resources afterwards. <c>ReceiveBody</c> calls it
    /// itself. Until it is called the source keeps the exchange uncommitted; a stopped template rolls
    /// back every exchange still open.
    /// </summary>
    /// <param name="exchange">An exchange this template handed out and has not completed yet.</param>
    /// <exception cref="InvalidOperationException">The exchange was not handed out by this template,
    /// was already completed, or was rolled back when the template stopped.</exception>
    Task DoneUoW(IExchange exchange);

    // ── Lifecycle ──

    /// <summary>Starts the consumer template. Must be called before any receive methods.</summary>
    void Start();

    /// <summary>
    /// Stops the consumer template and its cached consumers. Exchanges received but not completed
    /// with <see cref="DoneUoW"/>, and those the sources offered but no <c>Receive</c> took, are
    /// rolled back.
    /// </summary>
    void Stop();
}

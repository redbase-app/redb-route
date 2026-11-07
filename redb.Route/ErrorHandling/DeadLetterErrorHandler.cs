using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Processors;
using redb.Route.Telemetry;

namespace redb.Route.ErrorHandling;

/// <summary>
/// The context's dead letter channel — Apache Camel's <c>errorHandler(deadLetterChannel("direct:errors"))</c> at the
/// CamelContext level: set as <see cref="IRouteContext.ErrorHandler"/>, it takes every failure of every route that no
/// <c>OnException</c> took, stamps it with the <c>CamelDeadLetter*</c> headers and sends it to the dead letter endpoint
/// (usually a route of its own, <c>direct:errors</c>). A delivered exchange is handled — the failure does not go on to
/// the caller, as with Camel's dead letter channel. A failed delivery is logged and the failure goes on.
/// <para>
/// Redeliveries are the business of <c>OnException</c> (<c>MaximumRedeliveries</c>, <c>RetryWhile</c>): an error
/// handler receives a failure after the step that threw has returned and has nothing to run again.
/// </para>
/// </summary>
public sealed class DeadLetterErrorHandler : IErrorHandler
{
    private readonly IProcessor _deadLetter;
    private readonly ILogger? _logger;

    /// <summary>Creates the dead letter channel of <paramref name="context"/>.</summary>
    /// <param name="context">The context whose endpoint receives the dead letters.</param>
    /// <param name="deadLetterUri">The dead letter endpoint, e.g. <c>direct://errors</c>.</param>
    /// <param name="logger">Optional logger.</param>
    public DeadLetterErrorHandler(IRouteContext context, string deadLetterUri, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(deadLetterUri);
        DeadLetterUri = deadLetterUri;
        _deadLetter = new ToProcessor(deadLetterUri, context);
        _logger = logger;
    }

    /// <summary>The dead letter endpoint.</summary>
    public string DeadLetterUri { get; }

    /// <inheritdoc />
    public async Task HandleError(IExchange exchange, Exception exception, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(exchange);
        ArgumentNullException.ThrowIfNull(exception);

        DeadLetterHeaders.Stamp(exchange, exception, 0);
        _logger?.LogError(exception, "Dead-lettering exchange {ExchangeId} to {Uri}.",
            exchange.ExchangeId, EndpointUri.Sanitize(DeadLetterUri));
        try
        {
            await _deadLetter.Process(exchange, ct).ConfigureAwait(false);
            ProcessorMetrics.DeadLetterSent.Add(1);
            exchange.ExceptionHandled = true;
        }
        catch (Exception sendFailure) when (!RouteCancellation.IsCancellation(sendFailure, ct))
        {
            // The dead letter is lost: the original failure goes on to the caller rather than being swallowed.
            _logger?.LogError(sendFailure, "Dead-letter delivery to {Uri} failed.", EndpointUri.Sanitize(DeadLetterUri));
        }
    }
}

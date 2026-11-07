using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;

namespace redb.Route.Controllers;

/// <summary>
/// What a caller is told when an action fails for a reason the framework does not model, and where the
/// real reason goes instead. One place for all five dispatchers: each of them used to carry its own
/// copy of "put the exception's <c>Message</c> in the response", and a copy is exactly how one of them
/// stays wrong after the others are fixed.
/// <para>
/// The exception's text is written by whoever threw it and routinely carries internal detail — a file
/// path, a connection string, the name of an inner service. Handing that to a caller is a disclosure
/// (BR-4 in <c>redb.Tsak/docs/BOUNDARIES_AND_FOLLOWUPS.md</c> §1). The caller gets a generic sentence
/// and the exchange id to quote; the exception goes to the log under the same id, so an operator can
/// still find out what actually happened. Errors the framework itself produces (400 for a missing
/// header, 404 for no matching action) keep their text: it is ours and says nothing private.
/// </para>
/// </summary>
internal static class ControllerErrorReporting
{
    /// <summary>Error code reported for any unhandled action failure.</summary>
    public const string ErrorCode = "InternalError";

    /// <summary>Error code reported when the request could not be bound to the action's parameters.</summary>
    public const string BadRequestCode = "BadRequest";

    /// <summary>
    /// Logs a request-binding failure and returns the message the caller should see.
    /// </summary>
    /// <remarks>
    /// A body that does not bind is the CALLER's error: an expected event class, logged as a
    /// Warning without a stack — junk POSTed from outside must not fill the operator's log with
    /// error stacks (that is also a nudge-the-server-with-garbage log-flooding vector). And unlike
    /// action failures, the exception text here describes the caller's own bytes (the missing
    /// member, the unparsable token), so returning it is the BR-4 rule, not a violation of it.
    /// </remarks>
    public static string ReportBadRequest(ILogger? logger, Exception exception, IExchange exchange, string? operation)
    {
        logger?.LogWarning(
            "Malformed request for {Operation} (exchange {ExchangeId}): {Reason}",
            operation, exchange.ExchangeId, exception.Message);

        return exception.Message;
    }

    /// <summary>
    /// The dispatcher's logger. Looks in the context's own service table first and then in the DI
    /// container: <c>IRouteContext.GetService&lt;T&gt;()</c> reads only the table, and a host that builds
    /// the context as <c>new RouteContext(serviceProvider)</c> after <c>AddLogging()</c> never puts the
    /// factory there. Missing that second step is how withholding the exception from the caller turns
    /// into losing it altogether — worse for the operator than the disclosure it replaced.
    /// </summary>
    public static ILogger? CreateLogger<T>(IRouteContext context)
    {
        var factory = context.GetService<ILoggerFactory>()
                      ?? context.GetServiceProvider()?.GetService(typeof(ILoggerFactory)) as ILoggerFactory;
        return factory?.CreateLogger<T>();
    }

    /// <summary>
    /// Logs <paramref name="exception"/> against the exchange and returns the message the caller may
    /// see. Without a logger the detail is lost — that is the host's choice of configuration, and it is
    /// still better than disclosing it.
    /// </summary>
    /// <param name="logger">Dispatcher logger, or <c>null</c> when the context has no logger factory.</param>
    /// <param name="exception">The exception as thrown by the action (already unwrapped from any <c>TargetInvocationException</c>).</param>
    /// <param name="exchange">The exchange being dispatched; its id is the reference given to the caller.</param>
    /// <param name="operation">What was being dispatched, for the log line (e.g. <c>"GET modules/42"</c>).</param>
    public static string Report(ILogger? logger, Exception exception, IExchange exchange, string? operation)
    {
        logger?.LogError(exception,
            "Unhandled exception in controller action for {Operation} (exchange {ExchangeId})",
            operation, exchange.ExchangeId);

        return $"An unexpected error occurred while processing the request (ref: {exchange.ExchangeId}).";
    }
}

namespace redb.Route.Abstractions;

/// <summary>
/// Last-resort error handler invoked when no local or global exception handler matches.
/// Implement to define application-wide fallback error handling (e.g., dead-letter, alerting).
/// </summary>
public interface IErrorHandler
{
    /// <summary>
    /// Handles an exception that was not caught by any registered exception handler.
    /// </summary>
    /// <param name="exchange">The exchange that caused the exception.</param>
    /// <param name="exception">The unhandled exception.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task HandleError(IExchange exchange, Exception exception, CancellationToken ct = default);
}

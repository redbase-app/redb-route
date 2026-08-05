using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.ErrorHandling;

namespace redb.Route.Processors;

/// <summary>
/// Global exception handler. Registered at route level, handles exceptions
/// that aren't caught by per-step TryCatchProcessor.
/// Matches by exception type and executes handler processors.
/// Supports redelivery with configurable delay, exponential backoff, and max attempts.
/// </summary>
public class OnExceptionProcessor : IProcessor
{
    private readonly IProcessor _body;
    private readonly List<ExceptionHandler> _handlers = [];
    private readonly ILogger? _logger;

    /// <summary>Creates an on-exception processor wrapping the body.</summary>
    /// <param name="body">The main processor pipeline to protect.</param>
    /// <param name="logger">Optional logger for exception diagnostics.</param>
    public OnExceptionProcessor(IProcessor body, ILogger? logger = null)
    {
        _body = body ?? throw new ArgumentNullException(nameof(body));
        _logger = logger;
    }

    /// <summary>Registers a handler for a specific exception type.</summary>
    /// <typeparam name="TException">Exception type to handle.</typeparam>
    /// <param name="handler">Handler processor.</param>
    /// <param name="maxRedeliveries">Maximum retry attempts before giving up (default: 0, no retry).</param>
    /// <param name="redeliveryDelay">Delay between retry attempts (default: 1 second).</param>
    /// <param name="backoffMultiplier">Backoff multiplier (default: 1.0 = fixed delay).</param>
    /// <param name="useExponentialBackoff">Whether to apply exponential backoff (default: false).</param>
    /// <param name="handled">Whether the exception is marked as handled (default: false).</param>
    /// <param name="continued">Whether to continue the pipeline after the handler runs (default: false).</param>
    /// <param name="onWhenPredicate">Optional guard predicate.</param>
    /// <param name="retryAttemptedLogLevel">Log level for retry attempt messages (default: Warning).</param>
    /// <param name="retriesExhaustedLogLevel">Log level when retries are exhausted (default: Error).</param>
    /// <param name="onExceptionOccurred">Optional callback on each exception occurrence.</param>
    /// <param name="retryWhile">Optional predicate — retry continues while true.</param>
    /// <param name="onRedelivery">Optional callback before each redelivery.</param>
    /// <param name="onPrepareFailure">Optional callback before handler/DLQ after retries exhausted.</param>
    /// <param name="useOriginalMessage">Restore original message before retries and handler.</param>
    /// <param name="useOriginalBody">Restore original body only before retries and handler.</param>
    /// <param name="allowRedeliveryWhileStopping">Allow retries during cancellation.</param>
    /// <param name="logStackTrace">Include stack trace in retry logs.</param>
    /// <param name="logExhausted">Log when retries are exhausted.</param>
    /// <returns>This instance for fluent chaining.</returns>
    public OnExceptionProcessor Handle<TException>(
        IProcessor handler,
        int maxRedeliveries = 0,
        TimeSpan? redeliveryDelay = null,
        double backoffMultiplier = 1.0,
        bool useExponentialBackoff = false,
        bool handled = false,
        bool continued = false,
        Func<IExchange, bool>? onWhenPredicate = null,
        LogLevel retryAttemptedLogLevel = LogLevel.Warning,
        LogLevel retriesExhaustedLogLevel = LogLevel.Error,
        Action<IExchange>? onExceptionOccurred = null,
        Func<IExchange, bool>? retryWhile = null,
        Action<IExchange>? onRedelivery = null,
        Action<IExchange>? onPrepareFailure = null,
        bool useOriginalMessage = false,
        bool useOriginalBody = false,
        bool allowRedeliveryWhileStopping = false,
        bool logStackTrace = true,
        bool logExhausted = true)
        where TException : Exception
    {
        return Handle(typeof(TException), handler, maxRedeliveries, redeliveryDelay,
            backoffMultiplier, useExponentialBackoff, handled, continued,
            onWhenPredicate, retryAttemptedLogLevel, retriesExhaustedLogLevel, onExceptionOccurred,
            retryWhile, onRedelivery, onPrepareFailure, useOriginalMessage, useOriginalBody,
            allowRedeliveryWhileStopping, logStackTrace, logExhausted);
    }

    /// <summary>Registers a handler for a specific exception type (non-generic).</summary>
    public OnExceptionProcessor Handle(
        Type exceptionType,
        IProcessor handler,
        int maxRedeliveries = 0,
        TimeSpan? redeliveryDelay = null,
        double backoffMultiplier = 1.0,
        bool useExponentialBackoff = false,
        bool handled = false,
        bool continued = false,
        Func<IExchange, bool>? onWhenPredicate = null,
        LogLevel retryAttemptedLogLevel = LogLevel.Warning,
        LogLevel retriesExhaustedLogLevel = LogLevel.Error,
        Action<IExchange>? onExceptionOccurred = null,
        Func<IExchange, bool>? retryWhile = null,
        Action<IExchange>? onRedelivery = null,
        Action<IExchange>? onPrepareFailure = null,
        bool useOriginalMessage = false,
        bool useOriginalBody = false,
        bool allowRedeliveryWhileStopping = false,
        bool logStackTrace = true,
        bool logExhausted = true)
    {
        ArgumentNullException.ThrowIfNull(exceptionType);
        ArgumentNullException.ThrowIfNull(handler);

        RetryPolicy? retryPolicy = null;
        if (maxRedeliveries > 0)
        {
            var delay = redeliveryDelay ?? TimeSpan.FromSeconds(1);
            var multiplier = useExponentialBackoff ? Math.Max(backoffMultiplier, 2.0) : backoffMultiplier;
            retryPolicy = new RetryPolicy
            {
                MaxRetries = maxRedeliveries,
                InitialDelay = delay,
                BackoffMultiplier = multiplier,
                MaxDelay = TimeSpan.FromMinutes(5)
            };
        }

        _handlers.Add(new ExceptionHandler(exceptionType, handler, maxRedeliveries, retryPolicy,
            handled, continued, onWhenPredicate, retryAttemptedLogLevel, retriesExhaustedLogLevel,
            onExceptionOccurred, retryWhile, onRedelivery, onPrepareFailure,
            useOriginalMessage, useOriginalBody, allowRedeliveryWhileStopping,
            logStackTrace, logExhausted));
        return this;
    }

    /// <inheritdoc />
    public async Task Process(IExchange exchange, CancellationToken ct = default)
    {
        // Snapshot original message/body BEFORE processing (any handler may need it)
        object? originalBody = null;
        IDictionary<string, object?>? originalHeaders = null;

        var needsSnapshot = _handlers.Any(h => h.UseOriginalMessage || h.UseOriginalBody);
        if (needsSnapshot)
        {
            originalBody = exchange.In.Body;
            if (_handlers.Any(h => h.UseOriginalMessage))
                originalHeaders = new Dictionary<string, object?>(exchange.In.Headers);
        }

        var attempt = 0;
        while (true)
        {
            try
            {
                await _body.Process(exchange, ct).ConfigureAwait(false);
                return; // Success — exit
            }
            catch (OperationCanceledException)
            {
                throw; // Never swallow cancellation
            }
            catch (Exception ex)
            {
                exchange.Exception = ex;

                var handler = FindHandler(ex, exchange);
                if (handler == null)
                    throw; // No handler — rethrow

                // Invoke callback if registered
                handler.OnExceptionOccurred?.Invoke(exchange);

                // B10: Set redelivery headers
                exchange.In.Headers["CamelRedelivered"] = attempt > 0;
                exchange.In.Headers["CamelRedeliveryCounter"] = attempt;

                // Check if we should continue retrying
                var shouldRetry = false;
                if (handler.RetryWhile != null)
                {
                    // RetryWhile takes priority — retry as long as predicate returns true
                    shouldRetry = handler.RetryWhile(exchange);
                }
                else
                {
                    shouldRetry = attempt < handler.MaxRedeliveries;
                }

                // B7: Check if redelivery is allowed while stopping
                if (shouldRetry && !handler.AllowRedeliveryWhileStopping && ct.IsCancellationRequested)
                    shouldRetry = false;

                if (shouldRetry)
                {
                    attempt++;
                    exchange.In.Headers["CamelRedeliveryCounter"] = attempt;
                    exchange.In.Headers["CamelRedelivered"] = true;

                    if (handler.LogStackTrace)
                    {
                        _logger?.Log(handler.RetryAttemptedLogLevel, ex,
                            "Redelivery attempt {Attempt}/{Max} for {ExceptionType}",
                            attempt, handler.MaxRedeliveries, ex.GetType().Name);
                    }
                    else
                    {
                        _logger?.Log(handler.RetryAttemptedLogLevel,
                            "Redelivery attempt {Attempt}/{Max} for {ExceptionType}: {Message}",
                            attempt, handler.MaxRedeliveries, ex.GetType().Name,
                            EndpointUri.RedactSecrets(ex.Message));
                    }

                    // Apply redelivery delay
                    if (handler.RetryPolicy != null)
                    {
                        var delay = handler.RetryPolicy.GetDelay(attempt - 1);
                        if (delay > TimeSpan.Zero)
                            await Task.Delay(delay, ct).ConfigureAwait(false);
                    }

                    // B6: Restore original message/body before retry
                    RestoreOriginal(exchange, handler, originalBody, originalHeaders);

                    // B4: OnRedelivery callback before each retry
                    handler.OnRedelivery?.Invoke(exchange);

                    exchange.Exception = null;
                    exchange.ExceptionHandled = false;
                    continue; // Retry
                }

                // Retries exhausted — B10: set exhausted flag
                exchange.In.Headers["CamelRedeliveryExhausted"] = true;
                exchange.In.Headers["CamelRedeliveryMaxCounter"] = handler.MaxRedeliveries;

                // B8: Log exhausted
                if (handler.LogExhausted)
                {
                    if (handler.LogStackTrace)
                    {
                        _logger?.Log(handler.RetriesExhaustedLogLevel, ex,
                            "Retries exhausted for {ExceptionType} after {Attempts} attempts",
                            ex.GetType().Name, attempt);
                    }
                    else
                    {
                        _logger?.Log(handler.RetriesExhaustedLogLevel,
                            "Retries exhausted for {ExceptionType} after {Attempts} attempts: {Message}",
                            ex.GetType().Name, attempt, EndpointUri.RedactSecrets(ex.Message));
                    }

                    // Dump the message-history trail (when recorded) so the failing step is visible.
                    var history = Core.MessageHistory.Format(exchange);
                    if (!string.IsNullOrEmpty(history))
                        _logger?.Log(handler.RetriesExhaustedLogLevel, "{MessageHistory}", history);
                }

                // B6: Restore original message/body before handler
                RestoreOriginal(exchange, handler, originalBody, originalHeaders);

                // B5: OnPrepareFailure callback before handler/DLQ
                handler.OnPrepareFailure?.Invoke(exchange);

                await handler.Processor.Process(exchange, ct).ConfigureAwait(false);

                // Apply handled/continued flags
                if (handler.Handled)
                {
                    exchange.ExceptionHandled = true;
                    exchange.Exception = null;
                }
                else
                {
                    exchange.ExceptionHandled = true;
                }

                if (handler.Continued)
                {
                    exchange.ExceptionHandled = true;
                }

                return;
            }
        }
    }

    private static void RestoreOriginal(IExchange exchange, ExceptionHandler handler,
        object? originalBody, IDictionary<string, object?>? originalHeaders)
    {
        if (handler.UseOriginalMessage && originalHeaders != null)
        {
            exchange.In.Body = originalBody;
            exchange.In.Headers.Clear();
            foreach (var kvp in originalHeaders)
                exchange.In.Headers[kvp.Key] = kvp.Value;
        }
        else if (handler.UseOriginalBody)
        {
            exchange.In.Body = originalBody;
        }
    }

    private ExceptionHandler? FindHandler(Exception ex, IExchange exchange)
    {
        return _handlers.FirstOrDefault(h =>
            h.ExceptionType.IsInstanceOfType(ex) &&
            (h.OnWhenPredicate == null || h.OnWhenPredicate(exchange)));
    }

    /// <summary>Gets the registered handlers.</summary>
    public IReadOnlyList<ExceptionHandler> Handlers => _handlers;

    /// <summary>Describes a registered exception handler.</summary>
    /// <param name="ExceptionType">Exception type to match.</param>
    /// <param name="Processor">Handler processor to execute.</param>
    /// <param name="MaxRedeliveries">Maximum redelivery attempts before handler fires.</param>
    /// <param name="RetryPolicy">Optional retry policy for controlling delays between redeliveries.</param>
    /// <param name="Handled">Whether the exception is marked as handled (clears Exception on exchange).</param>
    /// <param name="Continued">Whether to continue the pipeline after handler.</param>
    /// <param name="OnWhenPredicate">Guard predicate — handler fires only when this returns true.</param>
    /// <param name="RetryAttemptedLogLevel">Log level for retry attempts.</param>
    /// <param name="RetriesExhaustedLogLevel">Log level when retries are exhausted.</param>
    /// <param name="OnExceptionOccurred">Callback invoked each time exception occurs.</param>
    /// <param name="RetryWhile">Predicate evaluated before each retry — continues retrying while true, regardless of max count.</param>
    /// <param name="OnRedelivery">Callback invoked before each redelivery attempt (after delay).</param>
    /// <param name="OnPrepareFailure">Callback invoked before the exchange is sent to the handler/DLQ after all retries are exhausted.</param>
    /// <param name="UseOriginalMessage">When true, restore the original message (body + headers) before each retry and before executing the handler.</param>
    /// <param name="UseOriginalBody">When true, restore only the original body (not headers) before each retry and before executing the handler.</param>
    /// <param name="AllowRedeliveryWhileStopping">When false (default), redelivery stops immediately if cancellation is requested.</param>
    /// <param name="LogStackTrace">Whether to include the full stack trace in retry log messages (default: true).</param>
    /// <param name="LogExhausted">Whether to log when all retries are exhausted (default: true).</param>
    public record ExceptionHandler(
        Type ExceptionType,
        IProcessor Processor,
        int MaxRedeliveries,
        RetryPolicy? RetryPolicy = null,
        bool Handled = false,
        bool Continued = false,
        Func<IExchange, bool>? OnWhenPredicate = null,
        LogLevel RetryAttemptedLogLevel = LogLevel.Warning,
        LogLevel RetriesExhaustedLogLevel = LogLevel.Error,
        Action<IExchange>? OnExceptionOccurred = null,
        Func<IExchange, bool>? RetryWhile = null,
        Action<IExchange>? OnRedelivery = null,
        Action<IExchange>? OnPrepareFailure = null,
        bool UseOriginalMessage = false,
        bool UseOriginalBody = false,
        bool AllowRedeliveryWhileStopping = false,
        bool LogStackTrace = true,
        bool LogExhausted = true);
}

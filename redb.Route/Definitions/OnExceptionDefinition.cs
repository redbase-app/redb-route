using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Processors;

namespace redb.Route.Definitions;

/// <summary>
/// Scope-opener definition for route-level exception handling.
/// Wraps the child pipeline with an <see cref="OnExceptionProcessor"/> that
/// retries, handles, or continues on the configured exception types.
/// Configure redelivery settings with <see cref="RedeliveryPolicy(Definitions.RedeliveryPolicy)"/>,
/// <see cref="MaximumRedeliveries"/>, etc., then close with <see cref="EndOnException"/> or
/// <see cref="IRouteScope.End"/>.
/// </summary>
public class OnExceptionDefinition : RouteDefinition, IRouteScope
{
    private readonly List<Type> _exceptionTypes;
    private int _maxRedeliveries;
    private TimeSpan? _redeliveryDelay;
    private double _backoffMultiplier = 1.0;
    private bool _useExponentialBackoff;
    private bool _handled;
    private bool _continued;

    // ── Public properties for test/inspection ─────────────────────────────────

    /// <summary>Exception types this handler applies to.</summary>
    public IReadOnlyList<Type> ExceptionTypes => _exceptionTypes;

    /// <summary>Maximum number of redelivery (retry) attempts.</summary>
    public int MaxRedeliveries => _maxRedeliveries;

    /// <summary>Delay between retry attempts (null = default 1 second). Inspection accessor.</summary>
    public TimeSpan? RedeliveryDelayValue => _redeliveryDelay;

    /// <summary>Backoff multiplier applied to the delay after each retry.</summary>
    public double BackoffMultiplierValue => _backoffMultiplier;

    /// <summary>Whether exponential backoff is enabled.</summary>
    public bool UseExponentialBackoffValue => _useExponentialBackoff;

    /// <summary>Whether the exception is marked as handled (suppresses propagation).</summary>
    public bool IsHandled => _handled;

    /// <summary>Whether exchange processing continues after the handler runs.</summary>
    public bool IsContinued => _continued;

    internal OnExceptionDefinition(IEnumerable<Type> exceptionTypes)
    {
        _exceptionTypes = [.. exceptionTypes];
    }

    // ── Configuration ─────────────────────────────────────────────────────────

    /// <summary>Applies all settings from a pre-built <see cref="Definitions.RedeliveryPolicy"/>.</summary>
    public OnExceptionDefinition RedeliveryPolicy(RedeliveryPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        _maxRedeliveries = policy.MaximumRedeliveries;
        _redeliveryDelay = policy.RedeliveryDelay;
        _backoffMultiplier = policy.BackOffMultiplier;
        _useExponentialBackoff = policy.UseExponentialBackOff;
        return this;
    }

    /// <summary>Sets the maximum number of redelivery attempts.</summary>
    public OnExceptionDefinition MaximumRedeliveries(int count)
    {
        _maxRedeliveries = count;
        return this;
    }

    /// <summary>Marks the exception as handled (suppresses further propagation).</summary>
    public OnExceptionDefinition Handled(bool value = true)
    {
        _handled = value;
        return this;
    }

    /// <summary>Continues exchange processing after the exception handler runs.</summary>
    public OnExceptionDefinition Continued(bool value = true)
    {
        _continued = value;
        return this;
    }

    /// <summary>Sets the delay between retry attempts.</summary>
    public OnExceptionDefinition RedeliveryDelay(TimeSpan delay)
    {
        _redeliveryDelay = delay;
        return this;
    }

    /// <summary>Sets the backoff multiplier applied to the delay after each retry.</summary>
    public OnExceptionDefinition BackOffMultiplier(double multiplier)
    {
        _backoffMultiplier = multiplier;
        return this;
    }

    /// <summary>Enables (or disables) exponential backoff between retries.</summary>
    public OnExceptionDefinition UseExponentialBackOff(bool value = true)
    {
        _useExponentialBackoff = value;
        return this;
    }

    /// <summary>Apache Camel alias for <see cref="BackOffMultiplier(double)"/>.</summary>
    public OnExceptionDefinition BackoffMultiplier(double multiplier) => BackOffMultiplier(multiplier);

    /// <summary>Apache Camel alias for <see cref="UseExponentialBackOff(bool)"/>.</summary>
    public OnExceptionDefinition UseExponentialBackoff(bool value = true) => UseExponentialBackOff(value);

    /// <summary>Marks that the original (pre-exception) body should be restored for the handler.</summary>
    public OnExceptionDefinition UseOriginalBody()
    {
        _useOriginalBody = true;
        return this;
    }
    private bool _useOriginalBody;
    /// <summary>Whether the original body restoration is enabled.</summary>
    public bool IsUseOriginalBody => _useOriginalBody;

    /// <summary>Conditional predicate — only handle when predicate returns true.</summary>
    public OnExceptionDefinition OnWhen(Func<IExchange, bool> predicate)
    {
        _onWhen = predicate ?? throw new ArgumentNullException(nameof(predicate));
        return this;
    }
    private Func<IExchange, bool>? _onWhen;
    /// <summary>Optional predicate gating handler activation.</summary>
    public Func<IExchange, bool>? OnWhenPredicate => _onWhen;

    /// <summary>Retry while the predicate returns true.</summary>
    public OnExceptionDefinition RetryWhile(Func<IExchange, bool> predicate)
    {
        _retryWhile = predicate ?? throw new ArgumentNullException(nameof(predicate));
        return this;
    }
    private Func<IExchange, bool>? _retryWhile;
    /// <summary>Optional predicate controlling retry continuation.</summary>
    public Func<IExchange, bool>? RetryWhilePredicate => _retryWhile;

    /// <summary>Callback invoked when the handler itself fails.</summary>
    public OnExceptionDefinition OnPrepareFailure(Action<IExchange> action)
    {
        _onPrepareFailure = action ?? throw new ArgumentNullException(nameof(action));
        return this;
    }
    private Action<IExchange>? _onPrepareFailure;
    /// <summary>Optional pre-failure callback.</summary>
    public Action<IExchange>? OnPrepareFailureAction => _onPrepareFailure;

    /// <summary>Callback invoked on each redelivery attempt.</summary>
    public OnExceptionDefinition OnRedelivery(Action<IExchange> action)
    {
        _onRedelivery = action ?? throw new ArgumentNullException(nameof(action));
        return this;
    }
    private Action<IExchange>? _onRedelivery;
    /// <summary>Optional redelivery callback.</summary>
    public Action<IExchange>? OnRedeliveryAction => _onRedelivery;

    /// <summary>Callback invoked when the exception occurs (before any retry).</summary>
    public OnExceptionDefinition OnExceptionOccurred(Action<IExchange> action)
    {
        _onExceptionOccurred = action ?? throw new ArgumentNullException(nameof(action));
        return this;
    }
    private Action<IExchange>? _onExceptionOccurred;
    /// <summary>Optional exception-occurred callback.</summary>
    public Action<IExchange>? OnExceptionOccurredAction => _onExceptionOccurred;

    /// <summary>Log level used for retry-attempt log entries.</summary>
    public OnExceptionDefinition RetryAttemptedLogLevel(LogLevel level)
    {
        _retryAttemptedLogLevel = level;
        return this;
    }
    private LogLevel _retryAttemptedLogLevel = LogLevel.Debug;
    /// <summary>Log level for retry-attempt log entries (default Debug).</summary>
    public LogLevel RetryAttemptedLogLevelValue => _retryAttemptedLogLevel;

    /// <summary>Log level used when retries are exhausted.</summary>
    public OnExceptionDefinition RetriesExhaustedLogLevel(LogLevel level)
    {
        _retriesExhaustedLogLevel = level;
        return this;
    }
    private LogLevel _retriesExhaustedLogLevel = LogLevel.Error;
    /// <summary>Log level used when retries are exhausted (default Error).</summary>
    public LogLevel RetriesExhaustedLogLevelValue => _retriesExhaustedLogLevel;

    /// <summary>Whether to include the full stack trace in retry log messages (default: true).</summary>
    public OnExceptionDefinition LogStackTrace(bool value = true)
    {
        _logStackTrace = value;
        return this;
    }
    private bool _logStackTrace = true;
    /// <summary>Current LogStackTrace value (default true).</summary>
    public bool LogStackTraceValue => _logStackTrace;

    /// <summary>Whether to emit a log entry when retries are exhausted (default: true).</summary>
    public OnExceptionDefinition LogExhausted(bool value = true)
    {
        _logExhausted = value;
        return this;
    }
    private bool _logExhausted = true;
    /// <summary>Current LogExhausted value (default true).</summary>
    public bool LogExhaustedValue => _logExhausted;

    // ── Leaf DSL (handler body) ───────────────────────────────────────────────

    /// <summary>Sends the exchange to an endpoint (handler body).</summary>
    public OnExceptionDefinition To(string uri) { AddOutput(new ToDefinition(uri)); return this; }

    /// <summary>Processes with a synchronous action (handler body).</summary>
    public OnExceptionDefinition Process(Action<IExchange> action) { AddOutput(new ProcessActionDefinition(action)); return this; }

    /// <summary>Processes with an asynchronous action (handler body).</summary>
    public OnExceptionDefinition Process(Func<IExchange, CancellationToken, Task> action) { AddOutput(new ProcessAsyncDefinition(action)); return this; }

    /// <summary>Processes with a pre-built processor (handler body).</summary>
    public OnExceptionDefinition Process(IProcessor processor) { AddOutput(new ProcessInstanceDefinition(processor)); return this; }

    /// <summary>Sets the exchange body to a static value (handler body).</summary>
    public OnExceptionDefinition SetBody(object? value) { AddOutput(new SetBodyStaticDefinition(value)); return this; }

    /// <summary>Sets the exchange body via a factory function (handler body).</summary>
    public OnExceptionDefinition SetBody(Func<IExchange, object?> factory) { AddOutput(new SetBodyFactoryDefinition(factory)); return this; }

    /// <summary>Sets a header (handler body).</summary>
    public OnExceptionDefinition SetHeader(string key, object? value) { AddOutput(new SetHeaderStaticDefinition(key, value)); return this; }

    /// <summary>Transforms the exchange body (handler body).</summary>
    public OnExceptionDefinition Transform(Func<IExchange, object?> transform) { AddOutput(new TransformDefinition(transform)); return this; }

    /// <summary>Logs a message (handler body).</summary>
    public OnExceptionDefinition Log(string message) { AddOutput(new LogStaticDefinition(message)); return this; }

    /// <summary>Stops exchange processing (handler body).</summary>
    public OnExceptionDefinition Stop() { AddOutput(new StopDefinition()); return this; }

    // ── Navigation ─────────────────────────────────────────────────────────────

    /// <summary>Closes this OnException scope and returns the parent route definition.</summary>
    public IRouteDefinition EndOnException()
        => (IRouteDefinition)(Parent ?? throw new InvalidOperationException(
            "EndOnException() called without a parent route."));

    /// <inheritdoc cref="IRouteScope.End"/>
    public IRouteDefinition End() => EndOnException();

    // ── IProcessorDefinition ──────────────────────────────────────────────────

    /// <inheritdoc />
    /// <remarks>
    /// Standalone compilation returns a plain pipeline of the handler steps
    /// (this OnException's <see cref="ProcessorDefinition.Outputs"/>). Wrapping the protected
    /// body with retry/handler semantics happens in <see cref="RouteDefinition.CreateProcessor"/>
    /// via the hoisting pass, which calls <see cref="WrapBody"/>.
    /// </remarks>
    public override IProcessor CreateProcessor(IRouteContext context)
        => TryCatchDefinition.BuildPipeline(Outputs, context);

    /// <summary>
    /// Wraps the supplied <paramref name="body"/> processor with an
    /// <see cref="OnExceptionProcessor"/> whose handler is this definition's pipeline of
    /// child <see cref="ProcessorDefinition.Outputs"/>. Used by the route compiler to hoist
    /// in-route OnException blocks into a try/handle envelope around the rest of the route.
    /// </summary>
    internal IProcessor WrapBody(IProcessor body, IRouteContext context)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(context);

        var loggerFactory = context.GetService<ILoggerFactory>();
        var logger = loggerFactory?.CreateLogger<OnExceptionProcessor>();
        var oeProc = new OnExceptionProcessor(body, logger);

        var handlerBody = TryCatchDefinition.BuildPipeline(Outputs, context);
        foreach (var type in _exceptionTypes)
        {
            oeProc.Handle(type, handlerBody,
                maxRedeliveries: _maxRedeliveries,
                redeliveryDelay: _redeliveryDelay,
                backoffMultiplier: _backoffMultiplier,
                useExponentialBackoff: _useExponentialBackoff,
                handled: _handled,
                continued: _continued,
                onWhenPredicate: _onWhen,
                retryAttemptedLogLevel: _retryAttemptedLogLevel,
                retriesExhaustedLogLevel: _retriesExhaustedLogLevel,
                onExceptionOccurred: _onExceptionOccurred,
                retryWhile: _retryWhile,
                onRedelivery: _onRedelivery,
                onPrepareFailure: _onPrepareFailure,
                useOriginalBody: _useOriginalBody,
                logStackTrace: _logStackTrace,
                logExhausted: _logExhausted);
        }
        return oeProc;
    }
}

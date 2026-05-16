using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;

namespace redb.Route.Definitions;

/// <summary>
/// Internal interface for exception-configurable route definitions.
/// Shared by <see cref="RouteDefinition"/>'s OnExceptionScope (route-level)
/// and <see cref="ExceptionRouteDefinition"/> (builder-level).
/// </summary>
internal interface IExceptionConfig
{
    Type ExceptionType { get; }
    int MaxRedeliveriesValue { get; set; }
    TimeSpan? RedeliveryDelayValue { get; set; }
    double BackoffMultiplierValue { get; set; }
    bool UseExponentialBackoffValue { get; set; }
    bool HandledValue { get; set; }
    bool ContinuedValue { get; set; }
    Func<IExchange, bool>? OnWhenPredicateValue { get; set; }
    LogLevel RetryAttemptedLogLevelValue { get; set; }
    LogLevel RetriesExhaustedLogLevelValue { get; set; }
    Action<IExchange>? OnExceptionOccurredCallback { get; set; }
    Func<IExchange, bool>? RetryWhilePredicateValue { get; set; }
    Action<IExchange>? OnRedeliveryCallbackValue { get; set; }
    Action<IExchange>? OnPrepareFailureCallbackValue { get; set; }
    bool UseOriginalMessageValue { get; set; }
    bool UseOriginalBodyValue { get; set; }
    bool AllowRedeliveryWhileStoppingValue { get; set; }
    bool LogStackTraceValue { get; set; }
    bool LogExhaustedValue { get; set; }
}

/// <summary>
/// Builder-level exception route definition. Registered via
/// <c>OnException&lt;T&gt;()</c> in <see cref="RouteBuilder"/> and compiled
/// into a global exception handler on the <see cref="IRouteContext"/>.
/// <para>
/// Unlike the route-level <c>OnException</c> scope, this definition is standalone
/// and does not require <c>EndOnException()</c> or a parent route.
/// </para>
/// </summary>
internal sealed class ExceptionRouteDefinition : RouteDefinition, IExceptionConfig
{
    public Type ExceptionType { get; }
    public int MaxRedeliveriesValue { get; set; }
    public TimeSpan? RedeliveryDelayValue { get; set; }
    public double BackoffMultiplierValue { get; set; } = 1.0;
    public bool UseExponentialBackoffValue { get; set; }
    public bool HandledValue { get; set; }
    public bool ContinuedValue { get; set; }
    public Func<IExchange, bool>? OnWhenPredicateValue { get; set; }
    public LogLevel RetryAttemptedLogLevelValue { get; set; } = LogLevel.Warning;
    public LogLevel RetriesExhaustedLogLevelValue { get; set; } = LogLevel.Error;
    public Action<IExchange>? OnExceptionOccurredCallback { get; set; }
    public Func<IExchange, bool>? RetryWhilePredicateValue { get; set; }
    public Action<IExchange>? OnRedeliveryCallbackValue { get; set; }
    public Action<IExchange>? OnPrepareFailureCallbackValue { get; set; }
    public bool UseOriginalMessageValue { get; set; }
    public bool UseOriginalBodyValue { get; set; }
    public bool AllowRedeliveryWhileStoppingValue { get; set; }
    public bool LogStackTraceValue { get; set; } = true;
    public bool LogExhaustedValue { get; set; } = true;

    /// <summary>Additional exception types that share this definition's configuration.</summary>
    internal List<Type>? LinkedTypes { get; set; }

    internal ExceptionRouteDefinition(Type exceptionType)
    {
        ExceptionType = exceptionType;
    }

    /// <summary>Creates clones for each linked type, copying all configuration.</summary>
    internal IEnumerable<ExceptionRouteDefinition> Expand()
    {
        yield return this;
        if (LinkedTypes == null) yield break;
        foreach (var t in LinkedTypes)
        {
            var clone = new ExceptionRouteDefinition(t)
            {
                MaxRedeliveriesValue = MaxRedeliveriesValue,
                RedeliveryDelayValue = RedeliveryDelayValue,
                BackoffMultiplierValue = BackoffMultiplierValue,
                UseExponentialBackoffValue = UseExponentialBackoffValue,
                HandledValue = HandledValue,
                ContinuedValue = ContinuedValue,
                OnWhenPredicateValue = OnWhenPredicateValue,
                RetryAttemptedLogLevelValue = RetryAttemptedLogLevelValue,
                RetriesExhaustedLogLevelValue = RetriesExhaustedLogLevelValue,
                OnExceptionOccurredCallback = OnExceptionOccurredCallback,
                RetryWhilePredicateValue = RetryWhilePredicateValue,
                OnRedeliveryCallbackValue = OnRedeliveryCallbackValue,
                OnPrepareFailureCallbackValue = OnPrepareFailureCallbackValue,
                UseOriginalMessageValue = UseOriginalMessageValue,
                UseOriginalBodyValue = UseOriginalBodyValue,
                AllowRedeliveryWhileStoppingValue = AllowRedeliveryWhileStoppingValue,
                LogStackTraceValue = LogStackTraceValue,
                LogExhaustedValue = LogExhaustedValue
            };
            // Copy steps from the primary definition
            foreach (var step in Steps)
                clone._steps.Add(step);
            yield return clone;
        }
    }
}

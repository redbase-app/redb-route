using System;
using System.Collections.Generic;
using redb.Route.Definitions;

namespace redb.Route.Validation;

/// <summary>
/// Validates route definition structure before compilation.
/// Catches configuration errors early rather than producing cryptic runtime failures.
/// </summary>
internal static class RouteDefinitionValidator
{
    /// <summary>
    /// Validates a route definition and throws <see cref="RouteValidationException"/> if any errors are found.
    /// </summary>
    /// <param name="definition">The route definition to validate.</param>
    public static void Validate(RouteDefinition definition)
    {
        var errors = new List<string>();
        ValidateSteps(definition.Steps, errors);

        if (errors.Count > 0)
            throw new RouteValidationException(definition.GetRouteId(), errors);
    }

    private static void ValidateSteps(IReadOnlyList<RouteStep> steps, List<string> errors)
    {
        foreach (var step in steps)
        {
            switch (step)
            {
                case ScatterGatherStep sg:
                    ValidateScatterGather(sg, errors);
                    break;

                case CircuitBreakerStep cb:
                    ValidateCircuitBreaker(cb, errors);
                    break;

                case ThrottleStep th:
                    if (th.MaxPerPeriod <= 0)
                        errors.Add("Throttle: MaxPerPeriod must be > 0.");
                    break;

                case KeyedThrottleStep kt:
                    if (kt.MaxPerPeriod <= 0)
                        errors.Add("KeyedThrottle: MaxPerPeriod must be > 0.");
                    break;

                case DebounceStep db:
                    if (db.QuietPeriod <= TimeSpan.Zero)
                        errors.Add("Debounce: QuietPeriod must be > 0.");
                    break;

                case ResequenceStep rs:
                    if (rs.BatchSize <= 0)
                        errors.Add("Resequencer: BatchSize must be > 0.");
                    break;

                case LoadBalanceStep lb:
                    if (lb.Endpoints is null or { Count: 0 })
                        errors.Add("LoadBalancer: at least one endpoint is required.");
                    break;
            }

            // Recurse into sub-steps
            if (step is CircuitBreakerStep cb2 && cb2.FallbackSteps is { Count: > 0 })
                ValidateSteps(cb2.FallbackSteps, errors);
            if (step is TracedStep ts)
                ValidateSteps(ts.SubSteps, errors);
            if (step is MeteredStep ms)
                ValidateSteps(ms.SubSteps, errors);
        }
    }

    private static void ValidateScatterGather(ScatterGatherStep sg, List<string> errors)
    {
        if (sg.StaticRecipients is null or { Length: 0 } && sg.DynamicRecipients is null)
            errors.Add("ScatterGather: at least one recipient (static or dynamic) is required.");

        if (sg.AggregationStrategy is null)
            errors.Add("ScatterGather: AggregationStrategy is required.");

        if (sg.MaxDegreeOfParallelism < 0)
            errors.Add("ScatterGather: MaxDegreeOfParallelism must be >= 0.");
    }

    private static void ValidateCircuitBreaker(CircuitBreakerStep cb, List<string> errors)
    {
        if (cb.FailureThreshold <= 0)
            errors.Add("CircuitBreaker: FailureThreshold must be > 0.");

        if (cb.ResetTimeout.HasValue && cb.ResetTimeout.Value <= TimeSpan.Zero)
            errors.Add("CircuitBreaker: ResetTimeout must be > 0 when specified.");

        if (cb.HalfOpenMaxCalls <= 0)
            errors.Add("CircuitBreaker: HalfOpenMaxCalls must be > 0.");
    }
}

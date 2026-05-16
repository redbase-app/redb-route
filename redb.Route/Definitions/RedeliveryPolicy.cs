namespace redb.Route.Definitions;

/// <summary>
/// Pre-configured redelivery policy that can be shared across multiple OnException handlers.
/// Store in <see cref="Abstractions.IRouteContext"/> properties for reuse.
/// </summary>
public sealed class RedeliveryPolicy
{
    /// <summary>Maximum number of redelivery (retry) attempts. Default: 0 (no retries).</summary>
    public int MaximumRedeliveries { get; init; }

    /// <summary>Delay between retry attempts. Default: 1 second.</summary>
    public TimeSpan RedeliveryDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Backoff multiplier applied to the delay after each retry. Default: 1.0 (fixed delay).</summary>
    public double BackOffMultiplier { get; init; } = 1.0;

    /// <summary>Whether to use exponential backoff. Default: false.</summary>
    public bool UseExponentialBackOff { get; init; }

    /// <summary>Collision avoidance factor (jitter). 0.0 = no jitter, 0.15 = ±15%. Default: 0.</summary>
    public double CollisionAvoidanceFactor { get; init; }

    /// <summary>Delay pattern string overriding standard delay. Format: "attempt:delayMs;...".</summary>
    public string? DelayPattern { get; init; }

    /// <summary>Whether to include stack trace in retry log messages. Default: true.</summary>
    public bool LogStackTrace { get; init; } = true;

    /// <summary>Whether to log when retries are exhausted. Default: true.</summary>
    public bool LogExhausted { get; init; } = true;

    /// <summary>Allow redelivery while the route is stopping. Default: false.</summary>
    public bool AllowRedeliveryWhileStopping { get; init; }

    /// <summary>
    /// Applies this policy to the given <see cref="IExceptionConfig"/> instance.
    /// </summary>
    internal void ApplyTo(IExceptionConfig config)
    {
        config.MaxRedeliveriesValue = MaximumRedeliveries;
        config.RedeliveryDelayValue = RedeliveryDelay;
        config.BackoffMultiplierValue = BackOffMultiplier;
        config.UseExponentialBackoffValue = UseExponentialBackOff;
        config.LogStackTraceValue = LogStackTrace;
        config.LogExhaustedValue = LogExhausted;
        config.AllowRedeliveryWhileStoppingValue = AllowRedeliveryWhileStopping;
    }
}

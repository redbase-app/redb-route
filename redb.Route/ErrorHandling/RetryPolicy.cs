namespace redb.Route.ErrorHandling;

/// <summary>
/// Defines a policy for retrying failed exchange processing.
/// </summary>
public sealed class RetryPolicy
{
    /// <summary>Maximum number of retry attempts. 0 means no retries.</summary>
    public int MaxRetries { get; init; }

    /// <summary>Initial delay between retries.</summary>
    public TimeSpan InitialDelay { get; init; } = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Multiplier for exponential backoff between retries.
    /// 1.0 = fixed delay, 2.0 = double each time.
    /// </summary>
    public double BackoffMultiplier { get; init; } = 2.0;

    /// <summary>Maximum delay cap between retries.</summary>
    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Collision avoidance factor for adding random jitter to delays.
    /// 0.0 = no jitter, 0.15 = ±15% of the calculated delay (Camel default).
    /// Jitter is applied after exponential/fixed calculation but before the max-delay cap.
    /// Uses <see cref="Random.Shared"/> for thread safety.
    /// </summary>
    public double CollisionAvoidanceFactor { get; init; }

    /// <summary>
    /// Delay pattern string that overrides exponential/fixed calculation for specific attempt ranges.
    /// Format: "attempt1:delayMs;attempt2:delayMs;..." (e.g., "1:1000;5:5000;10:30000").
    /// If the current attempt matches or exceeds a key, that delay is used.
    /// The highest matching key wins. When no key matches, standard calculation is used.
    /// </summary>
    public string? DelayPattern { get; init; }

    /// <summary>
    /// Predicate to determine if an exception is retryable.
    /// When null, all exceptions (except <see cref="OperationCanceledException"/>) are retried.
    /// </summary>
    public Func<Exception, bool>? RetryableExceptionPredicate { get; init; }

    /// <summary>Creates a retry policy for no retries (fail immediately).</summary>
    public static RetryPolicy None => new() { MaxRetries = 0 };

    /// <summary>Creates a fixed-delay retry policy.</summary>
    /// <param name="maxRetries">Maximum retry attempts.</param>
    /// <param name="delay">Fixed delay between retries.</param>
    public static RetryPolicy Fixed(int maxRetries, TimeSpan delay) => new()
    {
        MaxRetries = maxRetries,
        InitialDelay = delay,
        BackoffMultiplier = 1.0,
        MaxDelay = delay
    };

    /// <summary>Creates an exponential backoff retry policy.</summary>
    /// <param name="maxRetries">Maximum retry attempts.</param>
    /// <param name="initialDelay">Initial delay.</param>
    /// <param name="multiplier">Backoff multiplier (default: 2.0).</param>
    /// <param name="maxDelay">Maximum delay cap.</param>
    public static RetryPolicy Exponential(
        int maxRetries,
        TimeSpan initialDelay,
        double multiplier = 2.0,
        TimeSpan? maxDelay = null) => new()
    {
        MaxRetries = maxRetries,
        InitialDelay = initialDelay,
        BackoffMultiplier = multiplier,
        MaxDelay = maxDelay ?? TimeSpan.FromSeconds(60)
    };

    /// <summary>Calculates the delay for the given attempt number (0-based).</summary>
    /// <param name="attempt">Attempt number (0 = first retry).</param>
    /// <returns>Delay duration for this attempt.</returns>
    public TimeSpan GetDelay(int attempt)
    {
        // DelayPattern takes priority when set and a matching key exists
        if (DelayPattern is not null)
        {
            var patternDelay = ResolveDelayPattern(attempt);
            if (patternDelay.HasValue)
                return patternDelay.Value;
        }

        double delayMs;
        if (attempt <= 0)
            delayMs = InitialDelay.TotalMilliseconds;
        else
            delayMs = InitialDelay.TotalMilliseconds * Math.Pow(BackoffMultiplier, attempt);

        // Apply jitter before cap
        if (CollisionAvoidanceFactor > 0)
        {
            var jitterRange = delayMs * CollisionAvoidanceFactor;
            delayMs += (Random.Shared.NextDouble() * 2 - 1) * jitterRange;
            delayMs = Math.Max(0, delayMs);
        }

        var capped = Math.Min(delayMs, MaxDelay.TotalMilliseconds);
        return TimeSpan.FromMilliseconds(capped);
    }

    private TimeSpan? ResolveDelayPattern(int attempt)
    {
        // Format: "1:1000;5:5000;10:30000"
        // Find the highest key <= attempt+1 (1-based in the pattern)
        var oneBasedAttempt = attempt + 1;
        double? bestDelay = null;
        int bestKey = -1;

        foreach (var part in DelayPattern!.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var colonIdx = part.IndexOf(':');
            if (colonIdx <= 0) continue;
            if (!int.TryParse(part.AsSpan(0, colonIdx), out var key)) continue;
            if (!double.TryParse(part.AsSpan(colonIdx + 1), out var delayMs)) continue;

            if (key <= oneBasedAttempt && key > bestKey)
            {
                bestKey = key;
                bestDelay = delayMs;
            }
        }

        return bestDelay.HasValue ? TimeSpan.FromMilliseconds(bestDelay.Value) : null;
    }

    /// <summary>Returns whether the exception is eligible for retry.</summary>
    /// <param name="ex">The exception to evaluate.</param>
    /// <returns><c>true</c> if the exception should be retried.</returns>
    public bool ShouldRetry(Exception ex)
    {
        if (ex is OperationCanceledException)
            return false;

        return RetryableExceptionPredicate?.Invoke(ex) ?? true;
    }
}

using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Telemetry;

namespace redb.Route.ErrorHandling;

/// <summary>
/// Processor that retries the inner processor according to a <see cref="RetryPolicy"/>.
/// Uses exponential backoff with jitter. Respects cancellation at all points.
/// </summary>
public sealed class RetryProcessor : IProcessor
{
    private readonly IProcessor _inner;
    private readonly RetryPolicy _policy;
    private readonly ILogger? _logger;

    /// <summary>Creates a retry processor.</summary>
    /// <param name="inner">Processor to retry on failure.</param>
    /// <param name="policy">Retry policy configuration.</param>
    /// <param name="logger">Optional logger for retry events.</param>
    public RetryProcessor(IProcessor inner, RetryPolicy policy, ILogger? logger = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task Process(IExchange exchange, CancellationToken ct = default)
    {
        var attempt = 0;

        while (true)
        {
            exchange.Properties["RetryAttempt"] = attempt;

            try
            {
                await _inner.Process(exchange, ct).ConfigureAwait(false);
                if (attempt > 0)
                    ProcessorMetrics.RetrySuccess.Add(1);
                return; // Success
            }
            // A downstream timeout is an OperationCanceledException whose token (ours) is still live — a
            // failure to retry, not a cancellation. Named a TimeoutException so the policy sees it as one.
            // A genuine cancellation (the caller's token fired) is filtered out: never retried.
            catch (Exception ex) when (!RouteCancellation.IsCancellation(ex, ct))
            {
                var failure = RouteCancellation.Normalize(ex, ct);
                if (attempt >= _policy.MaxRetries || !_policy.ShouldRetry(failure))
                {
                    ProcessorMetrics.RetryExhausted.Add(1);
                    _logger?.LogError(failure,
                        "Exchange processing failed after {Attempts} attempt(s). No more retries.",
                        attempt + 1);
                    if (ReferenceEquals(failure, ex))
                        throw;          // a plain failure — keep its original stack
                    throw failure;      // a named timeout (TimeoutException)
                }

                ProcessorMetrics.RetryAttempts.Add(1);
                // The policy's delay as it is: jitter only when the policy asks for it (CollisionAvoidanceFactor, off by
                // default as Camel's collision avoidance), already capped by MaxDelay.
                var actualDelay = _policy.GetDelay(attempt);

                _logger?.LogWarning(
                    "Exchange processing failed (attempt {Attempt}/{MaxRetries}): {Error}. Retrying in {Delay:F0}ms.",
                    attempt + 1, _policy.MaxRetries, failure.Message, actualDelay.TotalMilliseconds);

                await Task.Delay(actualDelay, ct).ConfigureAwait(false);

                // Reset exchange error state before retry
                exchange.Exception = null;
                exchange.ExceptionHandled = false;

                attempt++;
            }
        }
    }
}

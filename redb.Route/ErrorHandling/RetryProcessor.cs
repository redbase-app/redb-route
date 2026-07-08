using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
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
            catch (OperationCanceledException)
            {
                throw; // Never retry cancellation
            }
            catch (Exception ex)
            {
                if (attempt >= _policy.MaxRetries || !_policy.ShouldRetry(ex))
                {
                    ProcessorMetrics.RetryExhausted.Add(1);
                    _logger?.LogError(ex,
                        "Exchange processing failed after {Attempts} attempt(s). No more retries.",
                        attempt + 1);
                    throw;
                }

                ProcessorMetrics.RetryAttempts.Add(1);
                var delay = _policy.GetDelay(attempt);

                // Add ±15% jitter to prevent thundering herd
                var jitter = delay.TotalMilliseconds * (Random.Shared.NextDouble() * 0.3 - 0.15);
                var actualDelay = TimeSpan.FromMilliseconds(delay.TotalMilliseconds + jitter);

                _logger?.LogWarning(
                    "Exchange processing failed (attempt {Attempt}/{MaxRetries}): {Error}. Retrying in {Delay:F0}ms.",
                    attempt + 1, _policy.MaxRetries, ex.Message, actualDelay.TotalMilliseconds);

                await Task.Delay(actualDelay, ct).ConfigureAwait(false);

                // Reset exchange error state before retry
                exchange.Exception = null;
                exchange.ExceptionHandled = false;

                attempt++;
            }
        }
    }
}

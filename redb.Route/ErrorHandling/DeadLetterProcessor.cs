using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Telemetry;

namespace redb.Route.ErrorHandling;

/// <summary>
/// Processor that routes failed exchanges to a dead letter channel.
/// When the inner processor throws and all retries are exhausted,
/// the exchange is forwarded to a configurable dead-letter endpoint.
/// Supports an optional <see cref="RetryPolicy"/> for redelivery attempts before dead-lettering.
/// </summary>
public sealed class DeadLetterProcessor : IProcessor
{
    private readonly IProcessor _inner;
    private readonly IProcessor _deadLetterTarget;
    private readonly RetryPolicy? _retryPolicy;
    private readonly ILogger? _logger;

    /// <summary>Creates a dead letter channel processor.</summary>
    /// <param name="inner">Primary processing pipeline.</param>
    /// <param name="deadLetterTarget">Processor to handle dead-lettered exchanges (typically a ToProcessor).</param>
    /// <param name="retryPolicy">Optional retry policy — retries before dead-lettering.</param>
    /// <param name="logger">Optional logger.</param>
    public DeadLetterProcessor(IProcessor inner, IProcessor deadLetterTarget,
        RetryPolicy? retryPolicy = null, ILogger? logger = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _deadLetterTarget = deadLetterTarget ?? throw new ArgumentNullException(nameof(deadLetterTarget));
        _retryPolicy = retryPolicy;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task Process(IExchange exchange, CancellationToken ct = default)
    {
        var maxRetries = _retryPolicy?.MaxRetries ?? 0;
        var attempt = 0;

        while (true)
        {
            try
            {
                await _inner.Process(exchange, ct).ConfigureAwait(false);
                return; // Success — no dead letter needed
            }
            // A downstream timeout is an OperationCanceledException whose token (ours) is still live — a
            // failure to retry and dead-letter, not a cancellation. Named a TimeoutException.
            catch (Exception ex) when (!RouteCancellation.IsCancellation(ex, ct))
            {
                var failure = RouteCancellation.Normalize(ex, ct);
                if (attempt < maxRetries && _retryPolicy != null && _retryPolicy.ShouldRetry(failure))
                {
                    attempt++;
                    _logger?.LogWarning(failure,
                        "DeadLetterChannel: retry {Attempt}/{Max} for {ExceptionType}",
                        attempt, maxRetries, failure.GetType().Name);

                    var delay = _retryPolicy.GetDelay(attempt - 1);
                    if (delay > TimeSpan.Zero)
                        await Task.Delay(delay, ct).ConfigureAwait(false);

                    exchange.Exception = null;
                    exchange.ExceptionHandled = false;
                    continue; // Retry
                }

                // Stamp the exchange with the failure info
                exchange.Exception = failure;
                exchange.In.Headers["CamelDeadLetterReason"] = failure.Message;
                exchange.In.Headers["CamelDeadLetterExceptionType"] = failure.GetType().FullName;
                exchange.In.Headers["CamelDeadLetterTimestamp"] = DateTimeOffset.UtcNow;
                if (attempt > 0)
                    exchange.In.Headers["CamelDeadLetterRedeliveryCount"] = attempt;

                _logger?.LogError(failure,
                    "DeadLetterChannel: routing to DLQ after {Attempts} attempts for {ExceptionType}",
                    attempt, failure.GetType().Name);

                // Route to dead letter channel
                ProcessorMetrics.DeadLetterSent.Add(1);
                await _deadLetterTarget.Process(exchange, ct).ConfigureAwait(false);
                exchange.ExceptionHandled = true;
                return;
            }
        }
    }
}

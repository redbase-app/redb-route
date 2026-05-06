using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;

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
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (attempt < maxRetries && _retryPolicy != null && _retryPolicy.ShouldRetry(ex))
                {
                    attempt++;
                    _logger?.LogWarning(ex,
                        "DeadLetterChannel: retry {Attempt}/{Max} for {ExceptionType}",
                        attempt, maxRetries, ex.GetType().Name);

                    var delay = _retryPolicy.GetDelay(attempt - 1);
                    if (delay > TimeSpan.Zero)
                        await Task.Delay(delay, ct).ConfigureAwait(false);

                    exchange.Exception = null;
                    exchange.ExceptionHandled = false;
                    continue; // Retry
                }

                // Stamp the exchange with the failure info
                exchange.Exception = ex;
                exchange.In.Headers["CamelDeadLetterReason"] = ex.Message;
                exchange.In.Headers["CamelDeadLetterExceptionType"] = ex.GetType().FullName;
                exchange.In.Headers["CamelDeadLetterTimestamp"] = DateTimeOffset.UtcNow;
                if (attempt > 0)
                    exchange.In.Headers["CamelDeadLetterRedeliveryCount"] = attempt;

                _logger?.LogError(ex,
                    "DeadLetterChannel: routing to DLQ after {Attempts} attempts for {ExceptionType}",
                    attempt, ex.GetType().Name);

                // Route to dead letter channel
                await _deadLetterTarget.Process(exchange, ct).ConfigureAwait(false);
                exchange.ExceptionHandled = true;
                return;
            }
        }
    }
}

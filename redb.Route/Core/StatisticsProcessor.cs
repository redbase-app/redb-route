using System.Diagnostics;
using redb.Route.Abstractions;

namespace redb.Route.Core;

/// <summary>
/// Processor decorator that records message statistics on the endpoint.
/// Wraps the inner processor: calls <see cref="IEndpointStatistics.RecordMessageIn"/> before processing,
/// <see cref="IEndpointStatistics.RecordMessageOut"/> on success,
/// and <see cref="IEndpointStatistics.RecordError()"/> on failure.
/// Also records processing time, body bytes, and exception details.
/// No-op if the endpoint does not implement <see cref="IEndpointStatistics"/>.
/// </summary>
internal sealed class StatisticsProcessor : IProcessor
{
    private readonly IProcessor _inner;
    private readonly IEndpointStatistics? _stats;

    /// <summary>Creates a statistics-recording processor wrapper.</summary>
    /// <param name="inner">The processor to wrap.</param>
    /// <param name="endpoint">The source endpoint (checked for <see cref="IEndpointStatistics"/>).</param>
    public StatisticsProcessor(IProcessor inner, IEndpoint endpoint)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _stats = endpoint as IEndpointStatistics;
    }

    /// <inheritdoc />
    public async Task Process(IExchange exchange, CancellationToken ct = default)
    {
        if (_stats is null)
        {
            await _inner.Process(exchange, ct).ConfigureAwait(false);
            return;
        }

        _stats.RecordMessageIn();

        // Estimate incoming body size
        var bodySize = EndpointBase<EndpointOptions>.EstimateBodySize(exchange.In.Body);
        if (bodySize > 0)
            _stats.RecordBytesIn(bodySize);

        var sw = Stopwatch.StartNew();
        try
        {
            await _inner.Process(exchange, ct).ConfigureAwait(false);
            sw.Stop();

            // Check for handled exceptions (set on exchange but not thrown)
            if (exchange.Exception is not null && !exchange.ExceptionHandled)
            {
                _stats.RecordError(exchange.Exception);
            }
            else
            {
                _stats.RecordMessageOut();

                // Detect retry attempts: RetryProcessor sets "RetryAttempt" property,
                // OnExceptionProcessor sets "CamelRedeliveryCounter" header.
                // If either > 0, the exchange succeeded after retries → record warning.
                var retryAttempt = exchange.GetProperty<int>("RetryAttempt");
                var redeliveryCounter = exchange.In.Headers.TryGetValue("CamelRedeliveryCounter", out var rc)
                    && rc is int rci ? rci : 0;
                var totalRetries = Math.Max(retryAttempt, redeliveryCounter);
                if (totalRetries > 0)
                    _stats.RecordWarning($"Succeeded after {totalRetries} retry attempt(s)");
            }
        }
        catch (Exception ex)
        {
            sw.Stop();
            _stats.RecordError(ex);
            throw;
        }
        finally
        {
            _stats.RecordProcessingTime(sw.Elapsed);
        }
    }
}

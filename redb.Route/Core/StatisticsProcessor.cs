using System.Diagnostics;
using redb.Route.Abstractions;

namespace redb.Route.Core;

/// <summary>
/// Processor decorator that records consumer-side statistics on the source endpoint.
/// Wraps the inner processor: calls <see cref="IEndpointStatistics.RecordMessageIn"/> before processing
/// and <see cref="IEndpointStatistics.RecordError()"/> on failure; also records processing time,
/// body bytes, and retry warnings. MessagesOut is deliberately NOT recorded here — it is
/// producer-side only (<see cref="CountedSend"/>), so an endpoint that plays both roles
/// (direct:, seda:) counts each send once instead of twice. A consumer endpoint's completed
/// count is <c>MessagesIn - Errors - Cancelled</c>.
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
                if (IsCooperativeCancellation(exchange.Exception, ct))
                    _stats.RecordCancelled();
                else
                    _stats.RecordError(exchange.Exception);
            }
            else
            {
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

            // A cancellation the caller asked for is not the route's failure (BR-10): a dashboard
            // closing mid-poll was turning healthy routes red on the Error-Prone panel and
            // degrading endpoint health for the five-minute error window. The error-handling layer
            // has always read OCE this way — retry never retries it, dead-letter never parks it —
            // and the statistics were the one layer still counting it. An OCE thrown while the
            // caller's token is live (an internal timeout) is the route failing to answer in time
            // and stays an error.
            if (IsCooperativeCancellation(ex, ct))
                _stats.RecordCancelled();
            else
                _stats.RecordError(ex);
            throw;
        }
        finally
        {
            _stats.RecordProcessingTime(sw.Elapsed);
        }
    }

    /// <summary>
    /// True when the exception is the caller's own cancellation arriving back: an
    /// <see cref="OperationCanceledException"/> while the token the caller handed in is cancelled.
    /// </summary>
    internal static bool IsCooperativeCancellation(Exception exception, CancellationToken ct)
        => exception is OperationCanceledException && ct.IsCancellationRequested;
}

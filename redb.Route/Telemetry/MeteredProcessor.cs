using System.Diagnostics;
using redb.Route.Abstractions;

namespace redb.Route.Telemetry;

/// <summary>
/// Wraps a pipeline processor to collect metrics: exchange count, duration, failures, inflight.
/// Designed to wrap the top-level pipeline of a compiled route.
/// </summary>
public sealed class MeteredProcessor : IProcessor
{
    private readonly IProcessor _inner;
    private readonly string _routeId;

    /// <summary>Creates a metered processor wrapper.</summary>
    /// <param name="inner">Inner processor (typically the route pipeline).</param>
    /// <param name="routeId">Route identifier for metric tags.</param>
    public MeteredProcessor(IProcessor inner, string routeId)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _routeId = routeId ?? throw new ArgumentNullException(nameof(routeId));
    }

    /// <inheritdoc />
    public async Task Process(IExchange exchange, CancellationToken ct = default)
    {
        var tags = new KeyValuePair<string, object?>("redb.route.id", _routeId);
        RouteMetrics.ExchangesInflight.Add(1, tags);
        var sw = Stopwatch.StartNew();

        try
        {
            await _inner.Process(exchange, ct).ConfigureAwait(false);
            RouteMetrics.ExchangesProcessed.Add(1, tags);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            RouteMetrics.ExchangesFailed.Add(1, tags);
            throw;
        }
        finally
        {
            sw.Stop();
            RouteMetrics.ExchangeDuration.Record(sw.Elapsed.TotalMilliseconds, tags);
            RouteMetrics.ExchangesInflight.Add(-1, tags);
        }
    }
}

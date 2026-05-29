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
    private readonly KeyValuePair<string, object?>[] _tags;

    /// <summary>Creates a metered processor wrapper.</summary>
    /// <param name="inner">Inner processor (typically the route pipeline).</param>
    /// <param name="routeId">Route identifier for metric tags.</param>
    /// <param name="endpointUri">Optional <c>From</c> endpoint URI; emitted as <c>redb.route.endpoint</c>.</param>
    /// <param name="endpointScheme">Optional endpoint scheme (e.g. <c>kafka</c>, <c>http</c>); emitted as <c>redb.route.scheme</c>.</param>
    public MeteredProcessor(IProcessor inner, string routeId, string? endpointUri = null, string? endpointScheme = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _routeId = routeId ?? throw new ArgumentNullException(nameof(routeId));

        var tagList = new List<KeyValuePair<string, object?>>(3)
        {
            new("redb.route.id", _routeId)
        };
        if (!string.IsNullOrEmpty(endpointUri))
            tagList.Add(new KeyValuePair<string, object?>("redb.route.endpoint", endpointUri));
        if (!string.IsNullOrEmpty(endpointScheme))
            tagList.Add(new KeyValuePair<string, object?>("redb.route.scheme", endpointScheme));
        _tags = tagList.ToArray();
    }

    /// <inheritdoc />
    public async Task Process(IExchange exchange, CancellationToken ct = default)
    {
        // `ReadOnlySpan<>` is a ref struct and cannot live across `await`. The
        // instrument overloads also accept `ReadOnlySpan<KeyValuePair<…>>`
        // implicitly created from the array, so passing the array argument
        // directly preserves the same zero-copy semantics without violating
        // span-safety rules.
        RouteMetrics.ExchangesInflight.Add(1, _tags);
        var sw = Stopwatch.StartNew();

        try
        {
            await _inner.Process(exchange, ct).ConfigureAwait(false);
            RouteMetrics.ExchangesProcessed.Add(1, _tags);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            RouteMetrics.ExchangesFailed.Add(1, _tags);
            throw;
        }
        finally
        {
            sw.Stop();
            RouteMetrics.ExchangeDuration.Record(sw.Elapsed.TotalMilliseconds, _tags);
            RouteMetrics.ExchangesInflight.Add(-1, _tags);
        }
    }
}

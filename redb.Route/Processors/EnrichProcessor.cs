using redb.Route.Abstractions;
using redb.Route.Core;

namespace redb.Route.Processors;

/// <summary>
/// Content Enricher: calls an external endpoint and merges the result into the current exchange.
/// Implements the Content Enricher EIP pattern. The producer is cached for the lifetime
/// of this processor and started on first use.
/// </summary>
public sealed class EnrichProcessor : IProcessor
{
    private readonly IRouteContext _context;
    private readonly string _resourceUri;
    private readonly Func<IExchange, IExchange, IExchange> _mergeStrategy;
    private IProducer? _producer;

    /// <summary>Creates an enrich processor.</summary>
    /// <param name="context">Route context for resolving the resource endpoint.</param>
    /// <param name="resourceUri">URI of the enrichment endpoint.</param>
    /// <param name="mergeStrategy">Function merging original and enrichment exchanges: (original, enriched) → merged.</param>
    public EnrichProcessor(
        IRouteContext context,
        string resourceUri,
        Func<IExchange, IExchange, IExchange> mergeStrategy)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _resourceUri = resourceUri ?? throw new ArgumentNullException(nameof(resourceUri));
        _mergeStrategy = mergeStrategy ?? throw new ArgumentNullException(nameof(mergeStrategy));
    }

    /// <inheritdoc />
    public async Task Process(IExchange exchange, CancellationToken ct = default)
    {
        var resourceExchange = exchange.CloneLinked();
        try
        {
            resourceExchange.Pattern = ExchangePattern.InOut;

            var producer = await GetOrCreateProducerAsync(ct).ConfigureAwait(false);
            await producer.Process(resourceExchange, ct).ConfigureAwait(false);

            // Merge the response back into the original exchange
            var merged = _mergeStrategy(exchange, resourceExchange);

            exchange.In.Body = merged.In.Body;
            foreach (var header in merged.In.Headers)
                exchange.In.Headers[header.Key] = header.Value;
            if (merged.Exception != null)
                exchange.Exception = merged.Exception;
        }
        finally
        {
            if (resourceExchange is IAsyncDisposable d) await d.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task<IProducer> GetOrCreateProducerAsync(CancellationToken ct)
    {
        if (_producer is not null) return _producer;
        var endpoint = _context.GetEndpoint(_resourceUri);
        _producer = endpoint.CreateProducer();
        await _producer.Start(ct).ConfigureAwait(false);
        (_context as RouteContext)?.TrackProducer(_producer);
        return _producer;
    }
}

/// <summary>
/// Poll Enricher: sends a request to an endpoint with a timeout, merging the result
/// into the current exchange. If the endpoint does not respond within the timeout,
/// the merge strategy receives a null exchange, allowing graceful handling.
/// The producer is cached for the lifetime of this processor.
/// </summary>
public sealed class PollEnrichProcessor : IProcessor
{
    private readonly IRouteContext _context;
    private readonly string _resourceUri;
    private readonly Func<IExchange, IExchange?, IExchange> _mergeStrategy;
    private readonly TimeSpan _timeout;
    private IProducer? _producer;

    /// <summary>Creates a poll enrich processor.</summary>
    /// <param name="context">Route context for resolving the resource endpoint.</param>
    /// <param name="resourceUri">URI of the polling endpoint.</param>
    /// <param name="mergeStrategy">Function merging original and polled exchanges: (original, polled?) → merged.</param>
    /// <param name="timeout">Timeout waiting for data (default: 1 second).</param>
    public PollEnrichProcessor(
        IRouteContext context,
        string resourceUri,
        Func<IExchange, IExchange?, IExchange> mergeStrategy,
        TimeSpan? timeout = null)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _resourceUri = resourceUri ?? throw new ArgumentNullException(nameof(resourceUri));
        _mergeStrategy = mergeStrategy ?? throw new ArgumentNullException(nameof(mergeStrategy));
        _timeout = timeout ?? TimeSpan.FromSeconds(1);
    }

    /// <inheritdoc />
    public async Task Process(IExchange exchange, CancellationToken ct = default)
    {
        IExchange? pollExchange = null;
        IExchange? resourceExchange = null;

        try
        {
            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(_timeout);

                pollExchange = exchange.CloneLinked();
                pollExchange.Pattern = ExchangePattern.InOut;

                var producer = GetOrCreateProducer();
                await producer.Process(pollExchange, timeoutCts.Token).ConfigureAwait(false);
                resourceExchange = pollExchange;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Timeout — resourceExchange stays null
            }

            // Merge (polled may be null if timeout)
            var merged = _mergeStrategy(exchange, resourceExchange);
            exchange.In.Body = merged.In.Body;
            foreach (var header in merged.In.Headers)
                exchange.In.Headers[header.Key] = header.Value;
            if (merged.Exception != null)
                exchange.Exception = merged.Exception;
        }
        finally
        {
            if (pollExchange is IAsyncDisposable d) await d.DisposeAsync().ConfigureAwait(false);
        }
    }

    private IProducer GetOrCreateProducer()
    {
        if (_producer is not null) return _producer;
        var endpoint = _context.GetEndpoint(_resourceUri);
        _producer = endpoint.CreateProducer();
        return _producer;
    }
}

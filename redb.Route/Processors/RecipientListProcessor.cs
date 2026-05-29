using redb.Route.Abstractions;
using redb.Route.Telemetry;

namespace redb.Route.Processors;

/// <summary>
/// Sends the exchange to a dynamic list of endpoints determined at runtime.
/// Unlike Multicast (static URI list), RecipientList computes URIs from the exchange.
/// Supports parallel/sequential processing and configurable aggregation.
/// Producers are cached per URI for the lifetime of this processor.
/// </summary>
internal sealed class RecipientListProcessor : IProcessor, IAsyncDisposable
{
    private readonly IRouteContext _context;
    private readonly Func<IExchange, IEnumerable<string>> _recipientListFactory;
    private readonly bool _parallelProcessing;
    private readonly bool _stopOnException;
    private readonly Func<IExchange, IExchange, IExchange>? _aggregationStrategy;
    private readonly Dictionary<string, IProducer> _producerCache = new(StringComparer.Ordinal);
    private readonly object _cacheLock = new();

    /// <summary>Creates a recipient list processor.</summary>
    /// <param name="context">Route context for resolving endpoints.</param>
    /// <param name="recipientListFactory">Function that returns target URIs from the exchange.</param>
    /// <param name="parallelProcessing">Whether to send to recipients in parallel.</param>
    /// <param name="stopOnException">Whether to stop on the first exception.</param>
    /// <param name="aggregationStrategy">Optional pair-wise aggregation of results.</param>
    public RecipientListProcessor(
        IRouteContext context,
        Func<IExchange, IEnumerable<string>> recipientListFactory,
        bool parallelProcessing = false,
        bool stopOnException = false,
        Func<IExchange, IExchange, IExchange>? aggregationStrategy = null)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _recipientListFactory = recipientListFactory ?? throw new ArgumentNullException(nameof(recipientListFactory));
        _parallelProcessing = parallelProcessing;
        _stopOnException = stopOnException;
        _aggregationStrategy = aggregationStrategy;
    }

    /// <inheritdoc />
    public async Task Process(IExchange exchange, CancellationToken ct = default)
    {
        var uris = _recipientListFactory(exchange).ToList();
        if (uris.Count == 0) return;

        ProcessorMetrics.RecipientListRecipients.Record(uris.Count);

        if (_parallelProcessing)
            await ProcessParallel(exchange, uris, ct).ConfigureAwait(false);
        else
            await ProcessSequential(exchange, uris, ct).ConfigureAwait(false);
    }

    private async Task ProcessSequential(IExchange exchange, List<string> uris, CancellationToken ct)
    {
        IExchange? aggregated = null;
        var copies = new List<IExchange>(uris.Count);

        try
        {
            foreach (var uri in uris)
            {
                ct.ThrowIfCancellationRequested();
                var copy = exchange.CloneLinked();
                copies.Add(copy);
                var producer = GetOrCreateProducer(uri);

                try
                {
                    await producer.Process(copy, ct).ConfigureAwait(false);

                    if (copy.Exception != null && _stopOnException)
                    {
                        exchange.Exception = copy.Exception;
                        return;
                    }

                    if (_aggregationStrategy != null)
                        aggregated = aggregated is null ? copy : _aggregationStrategy(aggregated, copy);
                }
                catch when (_stopOnException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    copy.Exception = ex;
                    if (_aggregationStrategy != null)
                        aggregated = aggregated is null ? copy : _aggregationStrategy(aggregated, copy);
                }
            }

            if (aggregated != null && _aggregationStrategy != null)
            {
                exchange.In.Body = aggregated.In.Body;
                foreach (var header in aggregated.In.Headers)
                    exchange.In.Headers[header.Key] = header.Value;
            }
        }
        finally
        {
            foreach (var copy in copies)
                if (copy is IAsyncDisposable d) await d.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task ProcessParallel(IExchange exchange, List<string> uris, CancellationToken ct)
    {
        var copies = new List<IExchange>(uris.Count);

        try
        {
            var tasks = uris.Select(async uri =>
            {
                var copy = exchange.Clone();
                lock (copies) copies.Add(copy);
                var producer = GetOrCreateProducer(uri);
                try
                {
                    await producer.Process(copy, ct).ConfigureAwait(false);
                }
                finally
                {
                    // Release DI scopes early; body/headers stay alive for aggregation
                    await copy.ReleaseScopes().ConfigureAwait(false);
                }
                return copy;
            }).ToList();

            IExchange[] results;
            if (_stopOnException)
            {
                results = await Task.WhenAll(tasks).ConfigureAwait(false);
            }
            else
            {
                var settled = await Task.WhenAll(tasks.Select(async t =>
                {
                    try { return await t.ConfigureAwait(false); }
                    catch (Exception ex)
                    {
                        var errExchange = exchange.Clone();
                        lock (copies) copies.Add(errExchange);
                        errExchange.Exception = ex;
                        await errExchange.ReleaseScopes().ConfigureAwait(false);
                        return errExchange;
                    }
                })).ConfigureAwait(false);
                results = settled.Where(r => r != null).ToArray();
            }

            if (_aggregationStrategy != null && results.Length > 0)
            {
                var aggregated = results[0];
                for (int i = 1; i < results.Length; i++)
                    aggregated = _aggregationStrategy(aggregated, results[i]);
                exchange.In.Body = aggregated.In.Body;
                foreach (var header in aggregated.In.Headers)
                    exchange.In.Headers[header.Key] = header.Value;
            }
        }
        finally
        {
            foreach (var copy in copies)
                if (copy is IAsyncDisposable d) await d.DisposeAsync().ConfigureAwait(false);
        }
    }

    private IProducer GetOrCreateProducer(string uri)
    {
        lock (_cacheLock)
        {
            if (_producerCache.TryGetValue(uri, out var cached))
                return cached;

            var endpoint = _context.GetEndpoint(uri);
            var producer = endpoint.CreateProducer();
            _producerCache[uri] = producer;
            return producer;
        }
    }

    /// <summary>Stops all cached producers.</summary>
    public async ValueTask DisposeAsync()
    {
        KeyValuePair<string, IProducer>[] snapshot;
        lock (_cacheLock)
        {
            snapshot = _producerCache.ToArray();
            _producerCache.Clear();
        }

        foreach (var kvp in snapshot)
        {
            try
            {
                await kvp.Value.Stop().ConfigureAwait(false);
            }
            catch
            {
                // Best-effort cleanup during disposal
            }
        }
    }
}

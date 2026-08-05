using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.Core;

namespace redb.Route.Processors;

/// <summary>
/// Routing Slip EIP: routes the exchange through a list of endpoints that is computed <b>once</b>
/// up front (the "slip"), then piped through sequentially. This is the static counterpart of the
/// Dynamic Router (<see cref="DynamicRouterProcessor"/>), which recomputes the next hop after every
/// step — mirroring Apache Camel, where "the Routing Slip will compute the slip beforehand … only
/// computed once".
/// <para>
/// Between hops the exchange is threaded with Pipeline semantics (the <c>Out</c> of one hop becomes
/// the <c>In</c> of the next), exactly like <see cref="PipelineProcessor"/>; the final hop's
/// <c>Out</c> is left intact so an InOut caller / RPC reply still receives it. Producers are cached
/// per resolved URI and registered with the context for graceful shutdown.
/// </para>
/// </summary>
internal sealed class RoutingSlipProcessor : IProcessor, IAsyncDisposable
{
    /// <summary>
    /// Exchange property carrying the endpoint currently being processed as the slip advances
    /// (Camel parity: <c>CamelSlipEndpoint</c>).
    /// </summary>
    public const string SlipEndpointProperty = "CamelSlipEndpoint";

    private readonly IRouteContext _context;
    private readonly Func<IExchange, IEnumerable<string>> _slipFactory;
    private readonly bool _ignoreInvalidEndpoints;
    private readonly ILogger? _logger;
    private readonly ConcurrentDictionary<string, IProducer> _producerCache = new(StringComparer.Ordinal);

    public RoutingSlipProcessor(
        IRouteContext context,
        Func<IExchange, IEnumerable<string>> slipFactory,
        bool ignoreInvalidEndpoints = false,
        ILogger? logger = null)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _slipFactory = slipFactory ?? throw new ArgumentNullException(nameof(slipFactory));
        _ignoreInvalidEndpoints = ignoreInvalidEndpoints;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task Process(IExchange exchange, CancellationToken ct = default)
    {
        // The slip is evaluated ONCE (Routing Slip, not Dynamic Router).
        var uris = _slipFactory(exchange).ToList();
        if (uris.Count == 0) return;

        for (var i = 0; i < uris.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (exchange.IsStopped) break;

            var uri = uris[i];

            IProducer producer;
            try
            {
                producer = await GetOrCreateProducerAsync(uri, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (_ignoreInvalidEndpoints)
            {
                _logger?.LogWarning(ex, "Routing Slip: ignoring invalid endpoint '{Uri}'", uri);
                continue;
            }

            exchange.Properties[SlipEndpointProperty] = uri;
            _logger?.LogDebug("Routing Slip: hop {Hop}/{Total} → {Uri}", i + 1, uris.Count, uri);

            await producer.Process(exchange, ct).ConfigureAwait(false);

            // Pipeline EIP: the Out of an intermediate hop becomes the In of the next; the final
            // hop's Out is left intact for an InOut caller (direct-vm / replyTo / RPC).
            if (exchange.HasOut && i < uris.Count - 1)
            {
                var outMsg = exchange.Out!;
                exchange.In.Body = outMsg.Body;
                exchange.In.ContentType = outMsg.ContentType;
                foreach (var (key, value) in outMsg.Headers)
                    exchange.In.Headers[key] = value;
                exchange.Out = null; // prevent a stale Out from re-merging on the next hop
            }
        }
    }

    private async Task<IProducer> GetOrCreateProducerAsync(string uri, CancellationToken ct)
    {
        if (_producerCache.TryGetValue(uri, out var cached))
            return cached;

        var endpoint = _context.GetEndpoint(uri);
        var producer = endpoint.CreateProducer();
        await producer.Start(ct).ConfigureAwait(false);
        (_context as RouteContext)?.TrackProducer(producer);

        if (!_producerCache.TryAdd(uri, producer))
        {
            // Lost the race — stop our duplicate and use the one already cached.
            await producer.Stop(ct).ConfigureAwait(false);
            return _producerCache[uri];
        }

        return producer;
    }

    /// <summary>Stops all cached producers (best-effort).</summary>
    public async ValueTask DisposeAsync()
    {
        foreach (var kvp in _producerCache)
        {
            try { await kvp.Value.Stop().ConfigureAwait(false); }
            catch { /* best-effort cleanup during disposal */ }
        }

        _producerCache.Clear();
    }
}

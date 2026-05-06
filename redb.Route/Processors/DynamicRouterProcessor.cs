using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.Core;

namespace redb.Route.Processors;

/// <summary>
/// Dynamic Router: evaluates a routing function on each exchange to determine the next endpoint.
/// The exchange is sent to the returned URI, then the routing function is called again.
/// Routing continues until the function returns <c>null</c> (slip routing).
/// Implements the Dynamic Router / Routing Slip EIP pattern.
/// Producers are cached per URI to avoid repeated creation for hot paths.
/// </summary>
internal sealed class DynamicRouterProcessor : IProcessor, IAsyncDisposable
{
    private readonly IRouteContext _context;
    private readonly Func<IExchange, string?> _routingFunction;
    private readonly ConcurrentDictionary<string, IProducer> _producerCache = new(StringComparer.Ordinal);
    private readonly ILogger? _logger;

    /// <summary>Creates a dynamic router processor.</summary>
    /// <param name="context">Route context for resolving endpoints.</param>
    /// <param name="routingFunction">
    /// Function that returns the next URI to route to, or <c>null</c> to stop routing.
    /// The function is called after each hop, so the exchange state may change between calls.
    /// </param>
    public DynamicRouterProcessor(
        IRouteContext context,
        Func<IExchange, string?> routingFunction,
        ILogger? logger = null)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _routingFunction = routingFunction ?? throw new ArgumentNullException(nameof(routingFunction));
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task Process(IExchange exchange, CancellationToken ct = default)
    {
        const int maxHops = 1000; // Safety guard against infinite loops
        int hops = 0;

        while (!ct.IsCancellationRequested && !exchange.IsStopped)
        {
            var nextUri = _routingFunction(exchange);
            if (nextUri is null)
                break;

            _logger?.LogDebug("DynamicRouter: hop {Hop} → {Uri}", hops + 1, nextUri);

            if (++hops > maxHops)
                throw new InvalidOperationException(
                    $"DynamicRouter exceeded maximum hop count ({maxHops}). " +
                    "Possible infinite routing loop detected.");

            var producer = await GetOrCreateProducerAsync(nextUri, ct).ConfigureAwait(false);
            await producer.Process(exchange, ct).ConfigureAwait(false);
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

        // Use TryAdd to avoid overwriting if another thread added concurrently
        if (!_producerCache.TryAdd(uri, producer))
        {
            // Another thread won the race — stop our duplicate and use theirs
            await producer.Stop(ct).ConfigureAwait(false);
            return _producerCache[uri];
        }

        return producer;
    }

    /// <summary>Stops all cached producers.</summary>
    public async ValueTask DisposeAsync()
    {
        foreach (var kvp in _producerCache)
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

        _producerCache.Clear();
    }
}

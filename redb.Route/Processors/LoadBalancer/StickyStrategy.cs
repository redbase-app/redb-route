using System;
using System.Collections.Generic;
using redb.Route.Abstractions;

namespace redb.Route.Processors.LoadBalancer;

/// <summary>
/// Sticky load balancer strategy.
/// Routes exchanges with the same key to the same endpoint using hash-based selection.
/// Falls back to round-robin when the key extractor returns null/empty.
/// Thread-safe.
/// </summary>
public sealed class StickyStrategy : ILoadBalancerStrategy
{
    private readonly Func<IExchange, string?> _keyExtractor;
    private readonly RoundRobinStrategy _fallback = new();

    /// <summary>
    /// Creates a sticky strategy with the given key extractor.
    /// </summary>
    /// <param name="keyExtractor">
    /// Extracts a routing key from the exchange (e.g., tenant ID, correlation ID).
    /// When the result is null or empty, falls back to round-robin.
    /// </param>
    public StickyStrategy(Func<IExchange, string?> keyExtractor)
    {
        _keyExtractor = keyExtractor ?? throw new ArgumentNullException(nameof(keyExtractor));
    }

    /// <inheritdoc />
    public string Select(IExchange exchange, IReadOnlyList<string> endpoints)
    {
        if (endpoints.Count == 0)
            throw new InvalidOperationException("No endpoints configured for load balancer.");

        var key = _keyExtractor(exchange);
        if (string.IsNullOrEmpty(key))
            return _fallback.Select(exchange, endpoints);

        // Stable hash → deterministic endpoint selection
        var hash = key.GetHashCode(StringComparison.Ordinal);
        var idx = ((hash % endpoints.Count) + endpoints.Count) % endpoints.Count;
        return endpoints[idx];
    }
}

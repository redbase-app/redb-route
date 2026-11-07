using System;
using System.Collections.Generic;
using System.Threading;
using redb.Route.Abstractions;

namespace redb.Route.Processors.LoadBalancer;

/// <summary>
/// Weighted round-robin load balancer strategy.
/// Endpoints with higher weights receive proportionally more traffic.
/// Thread-safe via lock-free <see cref="Interlocked.Increment(ref int)"/>.
/// </summary>
public sealed class WeightedStrategy : ILoadBalancerStrategy
{
    private readonly string[] _expandedEndpoints;
    private int _index = -1;

    /// <summary>
    /// Creates a weighted strategy from endpoint-weight pairs.
    /// </summary>
    /// <param name="endpointWeights">Tuples of (uri, weight). Weight must be &gt; 0.</param>
    public WeightedStrategy(IReadOnlyList<(string Uri, int Weight)> endpointWeights)
    {
        ArgumentNullException.ThrowIfNull(endpointWeights);
        if (endpointWeights.Count == 0)
            throw new ArgumentException("At least one endpoint is required.", nameof(endpointWeights));

        // Expand into weighted array: (A:3, B:1) → [A, A, A, B]
        var expanded = new List<string>();
        foreach (var (uri, weight) in endpointWeights)
        {
            if (weight <= 0)
                throw new ArgumentException($"Weight for '{uri}' must be positive, got {weight}.", nameof(endpointWeights));

            for (var i = 0; i < weight; i++)
                expanded.Add(uri);
        }

        _expandedEndpoints = expanded.ToArray();
    }

    /// <summary>
    /// Selects the next endpoint using weighted round-robin.
    /// Note: the <paramref name="endpoints"/> parameter is intentionally unused;
    /// weights and their corresponding URIs are fixed at construction time.
    /// </summary>
    public string Select(IExchange exchange, IReadOnlyList<string> endpoints)
    {
        var idx = Interlocked.Increment(ref _index);
        return _expandedEndpoints[((idx % _expandedEndpoints.Length) + _expandedEndpoints.Length) % _expandedEndpoints.Length];
    }
}

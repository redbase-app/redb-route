using System;
using System.Collections.Generic;
using System.Threading;
using redb.Route.Abstractions;

namespace redb.Route.Processors.LoadBalancer;

/// <summary>
/// Round-robin load balancer strategy.
/// Cycles through endpoints evenly using a lock-free rotating index.
/// </summary>
public sealed class RoundRobinStrategy : ILoadBalancerStrategy
{
    private int _index = -1;

    /// <inheritdoc />
    public string Select(IExchange exchange, IReadOnlyList<string> endpoints)
    {
        if (endpoints.Count == 0)
            throw new InvalidOperationException("No endpoints configured for load balancer.");

        var idx = Interlocked.Increment(ref _index);
        // Handle int overflow: ensure non-negative modulo
        return endpoints[((idx % endpoints.Count) + endpoints.Count) % endpoints.Count];
    }
}

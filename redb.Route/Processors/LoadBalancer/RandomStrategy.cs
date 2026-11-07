using System;
using System.Collections.Generic;
using redb.Route.Abstractions;

namespace redb.Route.Processors.LoadBalancer;

/// <summary>
/// Random load balancer strategy.
/// Selects a random endpoint for each exchange. Stateless and thread-safe.
/// </summary>
public sealed class RandomStrategy : ILoadBalancerStrategy
{
    /// <inheritdoc />
    public string Select(IExchange exchange, IReadOnlyList<string> endpoints)
    {
        if (endpoints.Count == 0)
            throw new InvalidOperationException("No endpoints configured for load balancer.");

        return endpoints[Random.Shared.Next(endpoints.Count)];
    }
}

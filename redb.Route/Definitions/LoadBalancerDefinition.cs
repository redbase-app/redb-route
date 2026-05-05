using System;
using System.Collections.Generic;
using System.Linq;
using redb.Route.Abstractions;
using redb.Route.Processors.LoadBalancer;

namespace redb.Route.Definitions;

/// <summary>
/// Internal builder for <see cref="ILoadBalancerDefinition"/>.
/// Collects configuration and exposes it for step creation.
/// </summary>
internal sealed class LoadBalancerDefinition : ILoadBalancerDefinition
{
    internal string[]? EndpointUris { get; private set; }
    internal ILoadBalancerStrategy? SelectedStrategy { get; private set; }

    /// <inheritdoc />
    public ILoadBalancerDefinition Endpoints(params string[] uris)
    {
        EndpointUris = uris ?? throw new ArgumentNullException(nameof(uris));
        return this;
    }

    /// <inheritdoc />
    public ILoadBalancerDefinition Strategy(ILoadBalancerStrategy strategy)
    {
        SelectedStrategy = strategy ?? throw new ArgumentNullException(nameof(strategy));
        return this;
    }

    /// <inheritdoc />
    public ILoadBalancerDefinition UseRoundRobin()
    {
        SelectedStrategy = new RoundRobinStrategy();
        return this;
    }

    /// <inheritdoc />
    public ILoadBalancerDefinition UseRandom()
    {
        SelectedStrategy = new RandomStrategy();
        return this;
    }

    /// <inheritdoc />
    public ILoadBalancerDefinition UseFailover()
    {
        SelectedStrategy = new FailoverStrategy();
        return this;
    }

    /// <inheritdoc />
    public ILoadBalancerDefinition UseSticky(Func<IExchange, string> correlationKeyExtractor)
    {
        ArgumentNullException.ThrowIfNull(correlationKeyExtractor);
        SelectedStrategy = new StickyStrategy(correlationKeyExtractor!);
        return this;
    }

    /// <inheritdoc />
    public ILoadBalancerDefinition UseWeighted(Dictionary<string, int> weights)
    {
        ArgumentNullException.ThrowIfNull(weights);
        var entries = weights.Select(kv => (kv.Key, kv.Value)).ToList();
        SelectedStrategy = new WeightedStrategy(entries);
        return this;
    }
}

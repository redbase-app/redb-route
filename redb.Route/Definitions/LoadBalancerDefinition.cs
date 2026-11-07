using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Processors;
using redb.Route.Processors.LoadBalancer;

namespace redb.Route.Definitions;

/// <summary>
/// Definition for a Load Balancer step. Collects configuration and creates a
/// <see cref="LoadBalancerProcessor"/> at route-build time.
/// </summary>
public sealed class LoadBalancerDefinition : ProcessorDefinition, ILoadBalancerDefinition
{
    /// <summary>Target endpoint URIs to balance across.</summary>
    public string[]? Endpoints { get; private set; }

    /// <summary>The load balancer strategy.</summary>
    public ILoadBalancerStrategy? Strategy { get; private set; }

    // ── ILoadBalancerDefinition — explicit to avoid name conflicts with properties ──

    ILoadBalancerDefinition ILoadBalancerDefinition.Endpoints(params string[] uris)
    {
        Endpoints = uris ?? throw new ArgumentNullException(nameof(uris));
        return this;
    }

    ILoadBalancerDefinition ILoadBalancerDefinition.Strategy(ILoadBalancerStrategy strategy)
    {
        Strategy = strategy ?? throw new ArgumentNullException(nameof(strategy));
        return this;
    }

    /// <inheritdoc />
    public ILoadBalancerDefinition UseRoundRobin()
    {
        Strategy = new RoundRobinStrategy();
        return this;
    }

    /// <inheritdoc />
    public ILoadBalancerDefinition UseRandom()
    {
        Strategy = new RandomStrategy();
        return this;
    }

    /// <inheritdoc />
    public ILoadBalancerDefinition UseFailover()
    {
        Strategy = new FailoverStrategy();
        return this;
    }

    /// <inheritdoc />
    public ILoadBalancerDefinition UseSticky(Func<IExchange, string> correlationKeyExtractor)
    {
        ArgumentNullException.ThrowIfNull(correlationKeyExtractor);
        Strategy = new StickyStrategy(correlationKeyExtractor!);
        return this;
    }

    /// <inheritdoc />
    public ILoadBalancerDefinition UseWeighted(Dictionary<string, int> weights)
    {
        ArgumentNullException.ThrowIfNull(weights);
        var entries = weights.Select(kv => (kv.Key, kv.Value)).ToList();
        Strategy = new WeightedStrategy(entries);
        return this;
    }

    /// <inheritdoc />
    public override IProcessor CreateProcessor(IRouteContext context)
    {
        if (Strategy is null)
            throw new InvalidOperationException(
                "Strategy is required for LoadBalance. Use UseRoundRobin(), UseFailover(), etc.");
        if (Endpoints is null || Endpoints.Length == 0)
            throw new InvalidOperationException("At least one endpoint is required for LoadBalance.");

        var logger = context.GetService<ILoggerFactory>()?.CreateLogger<LoadBalancerProcessor>();
        return new LoadBalancerProcessor(context, Endpoints, Strategy, logger);
    }
}

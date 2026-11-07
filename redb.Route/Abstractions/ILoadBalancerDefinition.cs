using System;

namespace redb.Route.Abstractions;

/// <summary>
/// Fluent builder for configuring a Load Balancer step.
/// Allows setting endpoints and strategy via explicit instance or shortcut methods.
/// </summary>
public interface ILoadBalancerDefinition
{
    /// <summary>Sets the target endpoint URIs to balance across.</summary>
    /// <param name="uris">One or more endpoint URIs.</param>
    ILoadBalancerDefinition Endpoints(params string[] uris);

    /// <summary>Sets a custom load balancer strategy.</summary>
    /// <param name="strategy">The strategy instance.</param>
    ILoadBalancerDefinition Strategy(ILoadBalancerStrategy strategy);

    /// <summary>Uses round-robin strategy (default).</summary>
    ILoadBalancerDefinition UseRoundRobin();

    /// <summary>Uses random strategy.</summary>
    ILoadBalancerDefinition UseRandom();

    /// <summary>Uses failover strategy (tries next endpoint on failure).</summary>
    ILoadBalancerDefinition UseFailover();

    /// <summary>Uses sticky strategy with the specified correlation key extractor.</summary>
    /// <param name="correlationKeyExtractor">Function extracting a correlation key from the exchange.</param>
    ILoadBalancerDefinition UseSticky(Func<IExchange, string> correlationKeyExtractor);

    /// <summary>Uses weighted strategy with the specified endpoint weights.</summary>
    /// <param name="weights">Dictionary mapping endpoint URI to relative weight.</param>
    ILoadBalancerDefinition UseWeighted(System.Collections.Generic.Dictionary<string, int> weights);
}

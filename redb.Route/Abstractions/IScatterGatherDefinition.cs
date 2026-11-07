using System;
using System.Collections.Generic;

namespace redb.Route.Abstractions;

/// <summary>
/// Fluent builder for configuring a Scatter-Gather step.
/// Allows setting recipients (static or dynamic), aggregation strategy,
/// timeout, parallelism, and error handling options.
/// </summary>
public interface IScatterGatherDefinition
{
    /// <summary>Sets the static list of recipient endpoint URIs.</summary>
    /// <param name="uris">Endpoint URIs to scatter to.</param>
    IScatterGatherDefinition Recipients(params string[] uris);

    /// <summary>Sets a dynamic recipient factory that resolves URIs from the exchange at runtime.</summary>
    /// <param name="factory">Function returning target URIs from the current exchange.</param>
    IScatterGatherDefinition Recipients(Func<IExchange, IEnumerable<string>> factory);

    /// <summary>Sets the mandatory pair-wise aggregation strategy: (accumulated, current) → merged.</summary>
    /// <param name="strategy">Aggregation function applied pair-wise to collected responses.</param>
    IScatterGatherDefinition AggregationStrategy(Func<IExchange, IExchange, IExchange> strategy);

    /// <summary>Sets the timeout for the entire scatter-gather operation.</summary>
    /// <param name="timeout">Maximum duration to wait for all responses.</param>
    IScatterGatherDefinition Timeout(TimeSpan timeout);

    /// <summary>Enables or disables parallel processing of recipients (default: true).</summary>
    /// <param name="parallel">True for parallel, false for sequential.</param>
    IScatterGatherDefinition ParallelProcessing(bool parallel = true);

    /// <summary>Sets the maximum degree of parallelism (0 = processor count).</summary>
    /// <param name="maxDop">Maximum concurrent sends.</param>
    IScatterGatherDefinition MaxDegreeOfParallelism(int maxDop);

    /// <summary>Enables or disables stop-on-exception behavior (default: false).</summary>
    /// <param name="stop">True to propagate exceptions immediately, false to aggregate partial results.</param>
    IScatterGatherDefinition StopOnException(bool stop = true);
}

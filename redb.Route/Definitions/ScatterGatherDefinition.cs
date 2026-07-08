using System;
using System.Collections.Generic;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Processors;

namespace redb.Route.Definitions;

/// <summary>
/// Definition for a Scatter-Gather step. Collects configuration and creates a
/// <see cref="ScatterGatherProcessor"/> at route-build time.
/// </summary>
public sealed class ScatterGatherDefinition : ProcessorDefinition, IScatterGatherDefinition
{
    /// <summary>Fixed recipient URIs (null when dynamic recipients are configured).</summary>
    public string[]? StaticRecipients { get; private set; }

    /// <summary>Runtime recipient factory (null when static recipients are configured).</summary>
    public Func<IExchange, IEnumerable<string>>? DynamicRecipients { get; private set; }

    /// <summary>Mandatory pair-wise aggregation: (accumulated, current) → merged.</summary>
    public Func<IExchange, IExchange, IExchange>? AggregationStrategy { get; private set; }

    /// <summary>Maximum duration to wait for all responses (default: zero = no timeout).</summary>
    public TimeSpan Timeout { get; private set; }

    /// <summary>Whether recipients are processed in parallel (default: true).</summary>
    public bool ParallelProcessing { get; private set; } = true;

    /// <summary>Maximum degree of parallelism (0 = processor count).</summary>
    public int MaxDegreeOfParallelism { get; private set; }

    /// <summary>Whether to stop immediately on first exception (default: false).</summary>
    public bool StopOnException { get; private set; }

    // ── IScatterGatherDefinition — explicit to avoid name conflicts with properties ──

    IScatterGatherDefinition IScatterGatherDefinition.Recipients(params string[] uris)
    {
        StaticRecipients = uris ?? throw new ArgumentNullException(nameof(uris));
        DynamicRecipients = null;
        return this;
    }

    IScatterGatherDefinition IScatterGatherDefinition.Recipients(Func<IExchange, IEnumerable<string>> factory)
    {
        DynamicRecipients = factory ?? throw new ArgumentNullException(nameof(factory));
        StaticRecipients = null;
        return this;
    }

    IScatterGatherDefinition IScatterGatherDefinition.AggregationStrategy(Func<IExchange, IExchange, IExchange> strategy)
    {
        AggregationStrategy = strategy ?? throw new ArgumentNullException(nameof(strategy));
        return this;
    }

    IScatterGatherDefinition IScatterGatherDefinition.Timeout(TimeSpan timeout)
    {
        Timeout = timeout >= TimeSpan.Zero
            ? timeout
            : throw new ArgumentOutOfRangeException(nameof(timeout), "Timeout must be non-negative.");
        return this;
    }

    IScatterGatherDefinition IScatterGatherDefinition.ParallelProcessing(bool parallel)
    {
        ParallelProcessing = parallel;
        return this;
    }

    IScatterGatherDefinition IScatterGatherDefinition.MaxDegreeOfParallelism(int maxDop)
    {
        MaxDegreeOfParallelism = maxDop >= 0
            ? maxDop
            : throw new ArgumentOutOfRangeException(nameof(maxDop), "Max DOP must be non-negative.");
        return this;
    }

    IScatterGatherDefinition IScatterGatherDefinition.StopOnException(bool stop)
    {
        StopOnException = stop;
        return this;
    }

    /// <inheritdoc />
    public override IProcessor CreateProcessor(IRouteContext context)
    {
        if (AggregationStrategy is null)
            throw new InvalidOperationException("AggregationStrategy is required for ScatterGather.");
        if (StaticRecipients is null && DynamicRecipients is null)
            throw new InvalidOperationException("Recipients are required for ScatterGather.");

        var logger = context.GetService<ILoggerFactory>()?.CreateLogger<ScatterGatherProcessor>();

        return StaticRecipients is not null
            ? new ScatterGatherProcessor(context, StaticRecipients, AggregationStrategy,
                ParallelProcessing, MaxDegreeOfParallelism, StopOnException, Timeout, logger)
            : new ScatterGatherProcessor(context, DynamicRecipients!, AggregationStrategy,
                ParallelProcessing, MaxDegreeOfParallelism, StopOnException, Timeout, logger);
    }
}

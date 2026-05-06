using System;
using System.Collections.Generic;
using redb.Route.Abstractions;

namespace redb.Route.Definitions;

/// <summary>
/// Internal builder for <see cref="IScatterGatherDefinition"/>.
/// Collects configuration and exposes it for step creation.
/// </summary>
internal sealed class ScatterGatherDefinition : IScatterGatherDefinition
{
    internal string[]? StaticRecipients { get; private set; }
    internal Func<IExchange, IEnumerable<string>>? DynamicRecipients { get; private set; }
    internal Func<IExchange, IExchange, IExchange>? Strategy { get; private set; }
    internal TimeSpan TimeoutValue { get; private set; }
    internal bool IsParallel { get; private set; } = true;
    internal int MaxDop { get; private set; }
    internal bool StopOnEx { get; private set; }

    /// <inheritdoc />
    public IScatterGatherDefinition Recipients(params string[] uris)
    {
        StaticRecipients = uris ?? throw new ArgumentNullException(nameof(uris));
        DynamicRecipients = null;
        return this;
    }

    /// <inheritdoc />
    public IScatterGatherDefinition Recipients(Func<IExchange, IEnumerable<string>> factory)
    {
        DynamicRecipients = factory ?? throw new ArgumentNullException(nameof(factory));
        StaticRecipients = null;
        return this;
    }

    /// <inheritdoc />
    public IScatterGatherDefinition AggregationStrategy(Func<IExchange, IExchange, IExchange> strategy)
    {
        Strategy = strategy ?? throw new ArgumentNullException(nameof(strategy));
        return this;
    }

    /// <inheritdoc />
    public IScatterGatherDefinition Timeout(TimeSpan timeout)
    {
        TimeoutValue = timeout >= TimeSpan.Zero
            ? timeout
            : throw new ArgumentOutOfRangeException(nameof(timeout), "Timeout must be non-negative.");
        return this;
    }

    /// <inheritdoc />
    public IScatterGatherDefinition ParallelProcessing(bool parallel = true)
    {
        IsParallel = parallel;
        return this;
    }

    /// <inheritdoc />
    public IScatterGatherDefinition MaxDegreeOfParallelism(int maxDop)
    {
        MaxDop = maxDop >= 0
            ? maxDop
            : throw new ArgumentOutOfRangeException(nameof(maxDop), "Max DOP must be non-negative.");
        return this;
    }

    /// <inheritdoc />
    public IScatterGatherDefinition StopOnException(bool stop = true)
    {
        StopOnEx = stop;
        return this;
    }
}

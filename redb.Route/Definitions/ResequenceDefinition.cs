using redb.Route.Abstractions;
using redb.Route.Processors;

namespace redb.Route.Definitions;

/// <summary>
/// Scope-opener definition for the Resequencer EIP. Buffers and reorders incoming
/// exchanges by a sequence key before delivering them to its child outputs.
/// Apache Camel parity: <c>.resequence(...).process(...).end()</c>.
/// Inherits the leaf DSL from <see cref="RouteDefinitionBase{TSelf}"/>.
/// </summary>
public sealed class ResequenceDefinition : RouteDefinitionBase<ResequenceDefinition>, IRouteScope
{
    private readonly Func<IExchange, long> _keySelector;
    private readonly int _batchSize;
    private readonly TimeSpan? _timeout;

    /// <summary>Creates a resequence definition.</summary>
    internal ResequenceDefinition(Func<IExchange, long> keySelector, int batchSize, TimeSpan? timeout)
    {
        ArgumentNullException.ThrowIfNull(keySelector);
        if (batchSize <= 0) throw new ArgumentOutOfRangeException(nameof(batchSize));
        _keySelector = keySelector;
        _batchSize = batchSize;
        _timeout = timeout;
    }

    /// <inheritdoc />
    public override IProcessor CreateProcessor(IRouteContext context)
    {
        IProcessor inner = Outputs.Count switch
        {
            0 => new DelegateProcessor(_ => { }),
            1 => Outputs[0].CreateProcessor(context),
            _ => BuildPipeline(context)
        };
        return new ResequencerProcessor(inner, _keySelector, _batchSize, _timeout);
    }

    private PipelineProcessor BuildPipeline(IRouteContext context)
    {
        var pipeline = new PipelineProcessor();
        foreach (var output in Outputs)
            pipeline.Add(output.CreateProcessor(context));
        return pipeline;
    }

    /// <summary>Closes the Resequence scope and returns to the parent route.</summary>
    public IRouteDefinition EndResequence()
        => (IRouteDefinition)(Parent ?? throw new InvalidOperationException(
            "EndResequence() called without a parent route. Ensure Resequence() was called on a route definition."));

    /// <inheritdoc cref="IRouteScope.End"/>
    public IRouteDefinition End() => EndResequence();
}

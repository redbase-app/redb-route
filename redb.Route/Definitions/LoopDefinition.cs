using redb.Route.Abstractions;
using redb.Route.Processors;

namespace redb.Route.Definitions;

/// <summary>
/// Scope-opener definition for the Loop EIP.
/// Repeats the body pipeline either a fixed number of times, a dynamic number of times
/// resolved from the exchange, or while a predicate holds. Inherits the leaf DSL from
/// <see cref="RouteDefinitionBase{TSelf}"/>; close with <see cref="EndLoop"/>.
/// </summary>
public class LoopDefinition : RouteDefinitionBase<LoopDefinition>, IRouteScope
{
    private enum LoopMode { Count, CountFactory, While }

    private readonly LoopMode _mode;
    private readonly int _count;
    private readonly Func<IExchange, int>? _countFactory;
    private readonly Func<IExchange, bool>? _condition;
    private readonly bool _copy;
    private readonly bool _shareScope;

    /// <summary>Creates a count-based loop definition.</summary>
    internal LoopDefinition(int count, bool copy, bool shareScope)
    {
        if (count < 0) throw new ArgumentOutOfRangeException(nameof(count), "Must be non-negative.");
        _mode = LoopMode.Count;
        _count = count;
        _copy = copy;
        _shareScope = shareScope;
    }

    /// <summary>Creates a dynamic-count loop definition.</summary>
    internal LoopDefinition(Func<IExchange, int> countFactory, bool copy, bool shareScope)
    {
        ArgumentNullException.ThrowIfNull(countFactory);
        _mode = LoopMode.CountFactory;
        _countFactory = countFactory;
        _copy = copy;
        _shareScope = shareScope;
    }

    /// <summary>Creates a while-condition loop definition.</summary>
    internal LoopDefinition(Func<IExchange, bool> condition, bool copy, bool shareScope)
    {
        ArgumentNullException.ThrowIfNull(condition);
        _mode = LoopMode.While;
        _condition = condition;
        _copy = copy;
        _shareScope = shareScope;
    }

    /// <summary>Whether each iteration receives an independent clone of the exchange.</summary>
    public bool Copy => _copy;

    // ── Navigation ─────────────────────────────────────────────────────────────

    /// <summary>Closes this loop scope and returns the parent route definition.</summary>
    public IRouteDefinition EndLoop()
        => (IRouteDefinition)(Parent ?? throw new InvalidOperationException(
            "EndLoop() called without a parent route."));

    /// <inheritdoc cref="IRouteScope.End"/>
    public IRouteDefinition End() => EndLoop();

    // ── IProcessorDefinition ───────────────────────────────────────────────────

    /// <inheritdoc />
    public override IProcessor CreateProcessor(IRouteContext context)
    {
        var body = BuildPipeline(Outputs, context);
        if (_mode == LoopMode.Count)
            return new LoopProcessor(body, _count, _copy, _shareScope);
        if (_mode == LoopMode.CountFactory)
            return new LoopProcessor(body, _countFactory!, _copy, _shareScope);
        return new LoopProcessor(body, _condition!, _copy, _shareScope);
    }

    private static IProcessor BuildPipeline(IList<IProcessorDefinition> outputs, IRouteContext context)
    {
        return outputs.Count switch
        {
            0 => new DelegateProcessor(_ => { }),
            1 => outputs[0].CreateProcessor(context),
            _ => BuildMulti(outputs, context)
        };
    }

    private static PipelineProcessor BuildMulti(IList<IProcessorDefinition> outputs, IRouteContext context)
    {
        var pipeline = new PipelineProcessor();
        foreach (var o in outputs)
            pipeline.Add(o.CreateProcessor(context));
        return pipeline;
    }
}

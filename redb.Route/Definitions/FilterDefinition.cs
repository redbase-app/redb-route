using redb.Route.Abstractions;
using redb.Route.Expressions;
using redb.Route.Processors;

namespace redb.Route.Definitions;

/// <summary>
/// Scope-opener definition for the Filter EIP.
/// When <see cref="CreateProcessor"/> is called, compiles all child <see cref="ProcessorDefinition.Outputs"/>
/// into an inner pipeline wrapped by a <see cref="FilterProcessor"/>.
/// Close the scope with <see cref="EndFilter"/>.
/// <para>
/// Inherits the entire fluent leaf-DSL from <see cref="RouteDefinitionBase{TSelf}"/>
/// with <typeparamref name="TSelf"/> = <see cref="FilterDefinition"/>; calls like
/// <c>To(...)</c>, <c>Process(...)</c>, <c>SetBody(...)</c> therefore return
/// <see cref="FilterDefinition"/> directly without locally-redeclared overloads.
/// </para>
/// </summary>
public class FilterDefinition : RouteDefinitionBase<FilterDefinition>, IRouteScope, ICompositeScope
{
    private readonly Func<IExchange, bool> _predicate;

    /// <summary>Captured source <see cref="IPredicate"/> when the filter was built from a predicate instance; null otherwise.</summary>
    public IPredicate? SourcePredicate { get; internal set; }

    /// <summary>Captured source <see cref="IExpression"/> when the filter was built from an expression instance; null otherwise.</summary>
    public IExpression? SourceExpression { get; internal set; }

    /// <summary>Captured source string template (e.g. <c>"${header.flag}"</c>) when the filter was built from a Simple expression; null otherwise.</summary>
    public string? SourceTemplate { get; internal set; }

    internal FilterDefinition(Func<IExchange, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        _predicate = predicate;
    }

    /// <inheritdoc />
    public override IProcessor CreateProcessor(IRouteContext context)
    {
        IProcessor body = Outputs.Count switch
        {
            0 => new DelegateProcessor(_ => { }),
            1 => Outputs[0].CreateProcessor(context),
            _ => BuildPipeline(context)
        };
        return new FilterProcessor(_predicate, body);
    }

    private PipelineProcessor BuildPipeline(IRouteContext context)
    {
        var pipeline = new PipelineProcessor();
        foreach (var output in Outputs)
            pipeline.Add(output.CreateProcessor(context));
        return pipeline;
    }

    /// <summary>Closes this filter scope and returns the parent route definition.</summary>
    /// <exception cref="InvalidOperationException">Thrown if no parent route is set.</exception>
    public IRouteDefinition EndFilter()
        => (IRouteDefinition)(Parent ?? throw new InvalidOperationException(
            "EndFilter() called without a parent route. Ensure Filter() was called on a route definition."));

    /// <inheritdoc cref="IRouteScope.End"/>
    public IRouteDefinition End() => EndFilter();
}

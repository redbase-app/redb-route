using redb.Route.Abstractions;
using redb.Route.Expressions;
using redb.Route.Predicates;
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
public class FilterDefinition : RouteDefinitionBase<FilterDefinition>, IRouteScope, ICompositeScope, IConditionSource
{
    private readonly IPredicate _predicate;

    /// <inheritdoc />
    public IPredicate? SourcePredicate { get; internal set; }

    /// <inheritdoc />
    public IExpression? SourceExpression { get; internal set; }

    /// <inheritdoc />
    public string? SourceTemplate { get; internal set; }

    internal FilterDefinition(IPredicate predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        _predicate = predicate;
    }

    internal FilterDefinition(Func<IExchange, bool> predicate)
        : this(new LambdaPredicate(predicate ?? throw new ArgumentNullException(nameof(predicate))))
    {
    }

    /// <inheritdoc />
    public override IProcessor CreateProcessor(IRouteContext context)
    {
        IProcessor body = NodePipeline.Body(context, Outputs);
        return new FilterProcessor(_predicate, body);
    }

    /// <summary>Closes this filter scope and returns the parent route definition.</summary>
    /// <exception cref="InvalidOperationException">Thrown if no parent route is set.</exception>
    public IRouteDefinition EndFilter()
        => (IRouteDefinition)(Parent ?? throw new InvalidOperationException(
            "EndFilter() called without a parent route. Ensure Filter() was called on a route definition."));

    /// <inheritdoc cref="IRouteScope.End"/>
    public IRouteDefinition End() => EndFilter();
}

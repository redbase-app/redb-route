using redb.Route.Abstractions;
using redb.Route.Processors;

namespace redb.Route.Definitions;

/// <summary>
/// Scope-opener definition for the Aggregator EIP.
/// Collects exchanges sharing the same correlation key, merges them via the aggregation strategy,
/// and forwards the completed aggregate to the body pipeline when the completion predicate is satisfied.
/// Inherits the leaf DSL from <see cref="RouteDefinitionBase{TSelf}"/>;
/// child steps build the <em>target</em> pipeline executed on completed aggregates.
/// Close with <see cref="EndAggregate"/>.
/// </summary>
public class AggregateDefinition : RouteDefinitionBase<AggregateDefinition>, IRouteScope
{
    private readonly Func<IExchange, string> _correlationKey;
    private readonly Func<IExchange, IExchange, IExchange> _aggregationStrategy;
    private readonly Func<IExchange, bool> _completionPredicate;

    internal AggregateDefinition(
        Func<IExchange, string> correlationKey,
        Func<IExchange, IExchange, IExchange> aggregationStrategy,
        Func<IExchange, bool> completionPredicate)
    {
        _correlationKey = correlationKey ?? throw new ArgumentNullException(nameof(correlationKey));
        _aggregationStrategy = aggregationStrategy ?? throw new ArgumentNullException(nameof(aggregationStrategy));
        _completionPredicate = completionPredicate ?? throw new ArgumentNullException(nameof(completionPredicate));
    }

    // ── Navigation ─────────────────────────────────────────────────────────────

    /// <summary>Closes the Aggregate scope and returns the parent route definition.</summary>
    public IRouteDefinition EndAggregate()
        => (IRouteDefinition)(Parent ?? throw new InvalidOperationException(
            "EndAggregate() called without a parent route."));

    /// <inheritdoc cref="IRouteScope.End"/>
    public IRouteDefinition End() => EndAggregate();

    // ── IProcessorDefinition ───────────────────────────────────────────────────

    /// <inheritdoc />
    public override IProcessor CreateProcessor(IRouteContext context)
    {
        IProcessor target = NodePipeline.Body(context, Outputs);
        return new AggregatorProcessor(_correlationKey, _aggregationStrategy, _completionPredicate, target);
    }
}

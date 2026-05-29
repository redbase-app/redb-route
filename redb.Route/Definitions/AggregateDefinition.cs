using redb.Route.Abstractions;
using redb.Route.Processors;

namespace redb.Route.Definitions;

/// <summary>
/// Scope-opener definition for the Aggregator EIP.
/// Collects exchanges sharing the same correlation key, merges them via the aggregation strategy,
/// and forwards the completed aggregate to the body pipeline when the completion predicate is satisfied.
/// Leaf methods on this definition build the <em>target</em> pipeline (executed on completed aggregates).
/// Close with <see cref="EndAggregate"/>.
/// </summary>
public class AggregateDefinition : RouteDefinition, IRouteScope
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
        IProcessor target = Outputs.Count switch
        {
            0 => new DelegateProcessor(_ => { }),
            1 => Outputs[0].CreateProcessor(context),
            _ => BuildPipeline(Outputs, context)
        };
        return new AggregatorProcessor(_correlationKey, _aggregationStrategy, _completionPredicate, target);
    }

    private static PipelineProcessor BuildPipeline(IList<IProcessorDefinition> outputs, IRouteContext context)
    {
        var pipeline = new PipelineProcessor();
        foreach (var o in outputs)
            pipeline.Add(o.CreateProcessor(context));
        return pipeline;
    }

    // ── Leaf DSL (target pipeline — runs on completed aggregates) ──────────────

    /// <summary>Sends the completed aggregate to an endpoint.</summary>
    public AggregateDefinition To(string uri) { AddOutput(new ToDefinition(uri)); return this; }

    /// <summary>Processes the completed aggregate with a synchronous action.</summary>
    public AggregateDefinition Process(Action<IExchange> action) { AddOutput(new ProcessActionDefinition(action)); return this; }

    /// <summary>Processes the completed aggregate with an asynchronous action.</summary>
    public AggregateDefinition Process(Func<IExchange, CancellationToken, Task> action) { AddOutput(new ProcessAsyncDefinition(action)); return this; }

    /// <summary>Processes the completed aggregate with a pre-built processor.</summary>
    public AggregateDefinition Process(IProcessor processor) { AddOutput(new ProcessInstanceDefinition(processor)); return this; }

    /// <summary>Sets the exchange body on the completed aggregate.</summary>
    public AggregateDefinition SetBody(object? value) { AddOutput(new SetBodyStaticDefinition(value)); return this; }

    /// <summary>Transforms the exchange body on the completed aggregate.</summary>
    public AggregateDefinition Transform(Func<IExchange, object?> transform) { AddOutput(new TransformDefinition(transform)); return this; }

    /// <summary>Sets a header on the completed aggregate.</summary>
    public AggregateDefinition SetHeader(string key, object? value) { AddOutput(new SetHeaderStaticDefinition(key, value)); return this; }

    /// <summary>Logs a message on the completed aggregate.</summary>
    public AggregateDefinition Log(string message) { AddOutput(new LogStaticDefinition(message)); return this; }

    /// <summary>Stops exchange processing.</summary>
    public AggregateDefinition Stop() { AddOutput(new StopDefinition()); return this; }
}

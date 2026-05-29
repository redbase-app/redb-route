using redb.Route.Abstractions;
using redb.Route.Expressions;
using redb.Route.Processors;

namespace redb.Route.Definitions;

/// <summary>
/// Scope-opener definition for the Filter EIP.
/// When <see cref="CreateProcessor"/> is called, compiles all child <see cref="ProcessorDefinition.Outputs"/>
/// into an inner pipeline wrapped by a <see cref="FilterProcessor"/>.
/// Close the scope with <see cref="EndFilter"/>.
/// </summary>
public class FilterDefinition : RouteDefinition, IRouteScope
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

    // ── Leaf DSL (mirror of RouteDefinition) ─────────────────────────────────

    /// <summary>Sends the exchange to an endpoint.</summary>
    public FilterDefinition To(string uri) { AddOutput(new ToDefinition(uri)); return this; }

    /// <summary>Processes the exchange with a synchronous action.</summary>
    public FilterDefinition Process(Action<IExchange> action) { AddOutput(new ProcessActionDefinition(action)); return this; }

    /// <summary>Processes the exchange with an asynchronous action.</summary>
    public FilterDefinition Process(Func<IExchange, CancellationToken, Task> action) { AddOutput(new ProcessAsyncDefinition(action)); return this; }

    /// <summary>Processes the exchange with a pre-built processor instance.</summary>
    public FilterDefinition Process(IProcessor processor) { AddOutput(new ProcessInstanceDefinition(processor)); return this; }

    /// <summary>Sets the exchange body to a static value.</summary>
    public FilterDefinition SetBody(object? value) { AddOutput(new SetBodyStaticDefinition(value)); return this; }

    /// <summary>Sets the exchange body using a factory.</summary>
    public FilterDefinition SetBody(Func<IExchange, object?> factory) { AddOutput(new SetBodyFactoryDefinition(factory)); return this; }

    /// <summary>Sets the exchange body using an <see cref="IExpression"/>.</summary>
    public FilterDefinition SetBody(IExpression expression) { AddOutput(new SetBodyExpressionDefinition(expression)); return this; }

    /// <summary>Transforms the exchange body.</summary>
    public FilterDefinition Transform(Func<IExchange, object?> transform) { AddOutput(new TransformDefinition(transform)); return this; }

    /// <summary>Removes the exchange body.</summary>
    public FilterDefinition RemoveBody() { AddOutput(new RemoveBodyDefinition()); return this; }

    /// <summary>Sets a header to a static value.</summary>
    public FilterDefinition SetHeader(string key, object? value) { AddOutput(new SetHeaderStaticDefinition(key, value)); return this; }

    /// <summary>Removes a header.</summary>
    public FilterDefinition RemoveHeader(string key) { AddOutput(new RemoveHeaderDefinition(key)); return this; }

    /// <summary>Sets a property to a static value.</summary>
    public FilterDefinition SetProperty(string key, object? value) { AddOutput(new SetPropertyStaticDefinition(key, value)); return this; }

    /// <summary>Removes a property.</summary>
    public FilterDefinition RemoveProperty(string key) { AddOutput(new RemovePropertyDefinition(key)); return this; }

    /// <summary>Stops exchange processing.</summary>
    public FilterDefinition Stop() { AddOutput(new StopDefinition()); return this; }
}

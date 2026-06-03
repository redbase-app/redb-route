using redb.Route.Abstractions;
using redb.Route.Processors;
using redb.Route.Telemetry;

namespace redb.Route.Definitions;

/// <summary>
/// Scope-opener definition for OpenTelemetry tracing.
/// All child steps run inside a single <see cref="System.Diagnostics.Activity"/> span
/// named <see cref="OperationName"/>. Transports (AMQP, RabbitMQ, etc.) create child spans
/// automatically from the parent activity propagated on the exchange.
/// Inherits the leaf DSL from <see cref="RouteDefinitionBase{TSelf}"/>;
/// close with <see cref="EndTraced"/> or the universal <see cref="End"/>.
/// </summary>
public class TracedDefinition : RouteDefinitionBase<TracedDefinition>, IRouteScope
{
    /// <summary>Operation name used as the Activity span name.</summary>
    public string OperationName { get; }

    internal TracedDefinition(string operationName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationName);
        OperationName = operationName;
    }

    // ── Navigation ─────────────────────────────────────────────────────────────

    /// <summary>Closes this traced scope and returns the parent route definition.</summary>
    /// <exception cref="InvalidOperationException">Thrown if no parent route is set.</exception>
    public IRouteDefinition EndTraced()
        => (IRouteDefinition)(Parent ?? throw new InvalidOperationException(
            "EndTraced() called without a parent route. Ensure Traced() was called on a route definition."));

    /// <inheritdoc cref="IRouteScope.End"/>
    public IRouteDefinition End() => EndTraced();

    // ── IProcessorDefinition ───────────────────────────────────────────────────

    /// <inheritdoc />
    public override IProcessor CreateProcessor(IRouteContext context)
    {
        IProcessor body = BuildPipeline(Outputs, context);
        return new InstrumentedProcessor(body, OperationName);
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

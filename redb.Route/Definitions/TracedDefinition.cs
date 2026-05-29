using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.Processors;
using redb.Route.Telemetry;
using redb.Route.Transactions;
using redb.Route.Validation;

namespace redb.Route.Definitions;

/// <summary>
/// Scope-opener definition for OpenTelemetry tracing.
/// All child steps run inside a single <see cref="System.Diagnostics.Activity"/> span
/// named <see cref="OperationName"/>. Transports (AMQP, RabbitMQ, etc.) create child spans
/// automatically from the parent activity propagated on the exchange.
/// Close with <see cref="EndTraced"/> or the universal <see cref="End"/>.
/// </summary>
public class TracedDefinition : RouteDefinition, IRouteScope
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

    // ── Leaf DSL ───────────────────────────────────────────────────────────────

    /// <summary>Sends the exchange to an endpoint.</summary>
    public TracedDefinition To(string uri) { AddOutput(new ToDefinition(uri)); return this; }

    /// <summary>Processes the exchange with a synchronous action.</summary>
    public TracedDefinition Process(Action<IExchange> action) { AddOutput(new ProcessActionDefinition(action)); return this; }

    /// <summary>Processes the exchange with an asynchronous action.</summary>
    public TracedDefinition Process(Func<IExchange, CancellationToken, Task> action) { AddOutput(new ProcessAsyncDefinition(action)); return this; }

    /// <summary>Processes the exchange with a pre-built processor instance.</summary>
    public TracedDefinition Process(IProcessor processor) { AddOutput(new ProcessInstanceDefinition(processor)); return this; }

    /// <summary>Sets the exchange body to a static value.</summary>
    public TracedDefinition SetBody(object? value) { AddOutput(new SetBodyStaticDefinition(value)); return this; }

    /// <summary>Transforms the exchange body.</summary>
    public TracedDefinition Transform(Func<IExchange, object?> transform) { AddOutput(new TransformDefinition(transform)); return this; }

    /// <summary>Sets a header to a static value.</summary>
    public TracedDefinition SetHeader(string key, object? value) { AddOutput(new SetHeaderStaticDefinition(key, value)); return this; }

    /// <summary>Sets a property to a static value.</summary>
    public TracedDefinition SetProperty(string key, object? value) { AddOutput(new SetPropertyStaticDefinition(key, value)); return this; }

    /// <summary>Logs a static message.</summary>
    public TracedDefinition Log(string message, LogLevel level = LogLevel.Information) { AddOutput(new LogStaticDefinition(message, level)); return this; }

    /// <summary>Stops exchange processing.</summary>
    public TracedDefinition Stop() { AddOutput(new StopDefinition()); return this; }

    /// <summary>Validates the exchange using a predicate function.</summary>
    public TracedDefinition Validate(Func<IExchange, bool> predicate, string errorMessage = "Validation failed", bool throwOnFailure = true)
    { AddOutput(new ValidatePredicateDefinition(predicate, errorMessage, throwOnFailure)); return this; }

    /// <summary>Begins a transaction scope.</summary>
    public TracedDefinition BeginTransaction(TransactionPolicy? policy = null)
    { AddOutput(new BeginTransactionDefinition(policy)); return this; }

    /// <summary>Commits the transaction.</summary>
    public TracedDefinition CommitTransaction() { AddOutput(new CommitTransactionDefinition()); return this; }

    /// <summary>Rolls back the transaction.</summary>
    public TracedDefinition RollbackTransaction() { AddOutput(new RollbackTransactionDefinition()); return this; }
}

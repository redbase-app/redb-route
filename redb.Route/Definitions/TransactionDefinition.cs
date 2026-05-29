using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.ErrorHandling;
using redb.Route.Processors;
using redb.Route.Transactions;
using redb.Route.Validation;

namespace redb.Route.Definitions;

/// <summary>
/// Scope-opener definition for declarative transaction management.
/// All child steps execute inside a <see cref="TransactedProcessor"/> which:
/// <list type="bullet">
/// <item>Wraps the inner pipeline in a <see cref="System.Transactions.TransactionScope"/>.</item>
/// <item>Auto-commits all <see cref="ITransactedAction"/> instances on success.</item>
/// <item>Auto-rolls-back on exception or cancellation.</item>
/// </list>
/// No explicit <c>CommitTransaction()</c> / <c>RollbackTransaction()</c> needed.
/// Close with <see cref="EndTransaction"/> or the universal <see cref="End"/>.
/// </summary>
public class TransactionDefinition : RouteDefinition, IRouteScope
{
    private readonly TransactionPolicy? _policy;
    private int _retryAttempts;
    private TimeSpan _retryDelay;
    private string? _deadLetterUri;

    /// <summary>The transaction policy this scope will use (null = default <c>Required</c>).</summary>
    public TransactionPolicy? Policy => _policy;

    internal TransactionDefinition(TransactionPolicy? policy = null)
    {
        _policy = policy;
    }

    // ── Camel-parity error handler hooks ───────────────────────────────────────

    /// <summary>
    /// Apache Camel parity: <c>errorHandler(defaultErrorHandler().maximumRedeliveries(n).redeliveryDelay(d))</c>.
    /// Retries the inner transactional body up to <paramref name="attempts"/> times before letting
    /// the exception propagate to the transaction (which will then roll back).
    /// </summary>
    public TransactionDefinition Retry(int attempts, TimeSpan delay)
    {
        if (attempts <= 0) throw new ArgumentOutOfRangeException(nameof(attempts));
        _retryAttempts = attempts;
        _retryDelay = delay;
        return this;
    }

    /// <summary>
    /// Apache Camel parity: <c>errorHandler(deadLetterChannel("..."))</c>.
    /// On failure the exchange is sent to <paramref name="deadLetterUri"/> after the transaction
    /// has rolled back.
    /// </summary>
    public TransactionDefinition DeadLetterChannel(string deadLetterUri)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deadLetterUri);
        _deadLetterUri = deadLetterUri;
        return this;
    }

    // ── Navigation ─────────────────────────────────────────────────────────────

    /// <summary>Closes this transaction scope and returns the parent route definition.</summary>
    /// <exception cref="InvalidOperationException">Thrown if no parent route is set.</exception>
    public IRouteDefinition EndTransaction()
        => (IRouteDefinition)(Parent ?? throw new InvalidOperationException(
            "EndTransaction() called without a parent route. Ensure Transaction() was called on a route definition."));

    /// <inheritdoc cref="IRouteScope.End"/>
    public IRouteDefinition End() => EndTransaction();

    // ── IProcessorDefinition ───────────────────────────────────────────────────

    /// <inheritdoc />
    public override IProcessor CreateProcessor(IRouteContext context)
    {
        IProcessor body = BuildPipeline(Outputs, context);
        if (_retryAttempts > 0)
        {
            var retryLogger = context.GetService<ILoggerFactory>()?.CreateLogger<RetryProcessor>();
            body = new RetryProcessor(body, RetryPolicy.Fixed(_retryAttempts, _retryDelay), retryLogger);
        }
        var policy = _policy ?? new TransactionPolicy();
        var logger = context.GetService<ILoggerFactory>()?.CreateLogger<TransactedProcessor>();
        IProcessor txProcessor = new TransactedProcessor(body, policy, logger);
        if (_deadLetterUri is not null)
        {
            var dlcLogger = context.GetService<ILoggerFactory>()?.CreateLogger<DeadLetterChannelProcessor>();
            txProcessor = new DeadLetterChannelProcessor(context, txProcessor, _deadLetterUri,
                rethrow: false, logger: dlcLogger);
        }
        return txProcessor;
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
    public TransactionDefinition To(string uri) { AddOutput(new ToDefinition(uri)); return this; }

    /// <summary>Processes the exchange with a synchronous action.</summary>
    public TransactionDefinition Process(Action<IExchange> action) { AddOutput(new ProcessActionDefinition(action)); return this; }

    /// <summary>Processes the exchange with an asynchronous action.</summary>
    public TransactionDefinition Process(Func<IExchange, CancellationToken, Task> action) { AddOutput(new ProcessAsyncDefinition(action)); return this; }

    /// <summary>Processes the exchange with a pre-built processor instance.</summary>
    public TransactionDefinition Process(IProcessor processor) { AddOutput(new ProcessInstanceDefinition(processor)); return this; }

    /// <summary>Sets the exchange body to a static value.</summary>
    public TransactionDefinition SetBody(object? value) { AddOutput(new SetBodyStaticDefinition(value)); return this; }

    /// <summary>Transforms the exchange body.</summary>
    public TransactionDefinition Transform(Func<IExchange, object?> transform) { AddOutput(new TransformDefinition(transform)); return this; }

    /// <summary>Sets a header to a static value.</summary>
    public TransactionDefinition SetHeader(string key, object? value) { AddOutput(new SetHeaderStaticDefinition(key, value)); return this; }

    /// <summary>Sets a property to a static value.</summary>
    public TransactionDefinition SetProperty(string key, object? value) { AddOutput(new SetPropertyStaticDefinition(key, value)); return this; }

    /// <summary>Logs a static message.</summary>
    public TransactionDefinition Log(string message, LogLevel level = LogLevel.Information) { AddOutput(new LogStaticDefinition(message, level)); return this; }

    /// <summary>Stops exchange processing.</summary>
    public TransactionDefinition Stop() { AddOutput(new StopDefinition()); return this; }

    /// <summary>Validates the exchange using a predicate function.</summary>
    public TransactionDefinition Validate(Func<IExchange, bool> predicate, string errorMessage = "Validation failed", bool throwOnFailure = true)
    { AddOutput(new ValidatePredicateDefinition(predicate, errorMessage, throwOnFailure)); return this; }
}

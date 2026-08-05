using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.ErrorHandling;
using redb.Route.Processors;
using redb.Route.Transactions;

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
/// Inherits the leaf DSL from <see cref="RouteDefinitionBase{TSelf}"/>;
/// close with <see cref="EndTransaction"/> or the universal <see cref="End"/>.
/// </summary>
public class TransactionDefinition : RouteDefinitionBase<TransactionDefinition>, IRouteScope, IDurableScope
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
        => NodePipeline.Body(context, outputs);
}

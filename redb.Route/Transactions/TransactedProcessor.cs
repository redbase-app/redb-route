using System.Collections.Concurrent;
using System.Transactions;
using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.Core;

namespace redb.Route.Transactions;

/// <summary>
/// Wraps an inner processor in a <see cref="TransactionScope"/> and manages
/// deferred <see cref="ITransactedAction"/> commit/rollback.
/// <para>
/// The block owns a <see cref="ConcurrentDictionary{TKey,TValue}"/> of deferred actions in
/// <c>exchange.Properties["TRANSACT_ACTION"]</c> for as long as it runs. Producers inside the block (Kafka, RabbitMQ,
/// etc.) add their sends through <see cref="TransactedActions.Register"/>, keyed by a per-message-unique
/// name (e.g. <c>kafka-send-{guid}</c>, never the endpoint URI, so parallel fan-out branches to the same endpoint
/// never collide). After the database commits all actions are committed; on failure they are rolled back.
/// </para>
/// <para>
/// Blocks nest as in Camel: a Required or Mandatory block inside a running transaction is part of that unit of work
/// and leaves its sends to the enclosing block; a RequiresNew or Suppress block is a unit of work of its own and
/// settles only its own sends.
/// </para>
/// </summary>
public sealed class TransactedProcessor : IProcessor
{
    /// <summary>
    /// Well-known exchange property key where transports store their <see cref="ITransactedAction"/> instances.
    /// </summary>
    public const string TransactActionPropertyKey = "TRANSACT_ACTION";

    private readonly IProcessor _inner;
    private readonly TransactionPolicy _policy;
    private readonly ILogger? _logger;

    /// <summary>Creates a transacted processor.</summary>
    /// <param name="inner">Inner pipeline to wrap in a transaction.</param>
    /// <param name="policy">Transaction policy (scope option, timeout, isolation level).</param>
    /// <param name="logger">Optional logger.</param>
    public TransactedProcessor(IProcessor inner, TransactionPolicy policy, ILogger? logger = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task Process(IExchange exchange, CancellationToken ct = default)
    {
        if (TransactedActions.JoinsEnclosingBlock(exchange, _policy))
        {
            await ProcessJoined(exchange, ct).ConfigureAwait(false);
            return;
        }

        // A unit of work of its own keeps its own set of deferred actions for as long as it runs and puts the enclosing
        // block's set back when it ends. A transacted step after the block, or on a route with no block at all, then
        // finds no set and fails (TransactedActions.Register) instead of deferring a write that nobody would commit.
        var enclosing = TransactedActions.Open(exchange);
        try
        {
            await ProcessUnitOfWork(exchange, ct).ConfigureAwait(false);
        }
        finally
        {
            TransactedActions.Close(exchange, enclosing);
        }
    }

    /// <summary>
    /// A Required or Mandatory block inside a running transaction: the database work joins the enclosing transaction
    /// and the sends stay in the enclosing set, so both commit with the enclosing block and in its order.
    /// </summary>
    private async Task ProcessJoined(IExchange exchange, CancellationToken ct)
    {
        using var scope = _policy.CreateScope();

        await _inner.Process(exchange, ct).ConfigureAwait(false);

        if (exchange.IsRollbackOnly())
        {
            // .RollbackAll(): leaving without Complete marks the enclosing transaction for rollback, and the enclosing
            // block, which sees the same mark, rolls everything back.
            _logger?.LogDebug("Joined transaction marked for rollback: the route marked the exchange rollback-only.");
            return;
        }

        if (exchange.Exception is { } failure && !exchange.ExceptionHandled)
        {
            // Leaving the scope without Complete marks the enclosing transaction for rollback: the enclosing block rolls
            // back the database and every deferred send with it.
            _logger?.LogError(failure,
                "Joined transaction marked for rollback: the exchange completed with an unhandled exception: {Message}",
                failure.Message);
            return;
        }

        scope.Complete();
    }

    private async Task ProcessUnitOfWork(IExchange exchange, CancellationToken ct)
    {
        _logger?.LogDebug(
            "Transaction started (ScopeOption={ScopeOption}, IsolationLevel={IsolationLevel}, Timeout={Timeout}).",
            _policy.ScopeOption, _policy.IsolationLevel, _policy.Timeout);

        // The database commits first and the brokers after it, because that is the order the next service reads the
        // work in: a message tells it something happened and it then looks the work up by id. The failure window is
        // therefore "written but not announced" — the broker redelivers and the idempotent consumer absorbs the
        // duplicate — never "announced but not written", which would point a message at rows nobody can see.
        try
        {
            using (var scope = _policy.CreateScope())
            {
                await _inner.Process(exchange, ct).ConfigureAwait(false);

                if (exchange.IsRollbackOnly())
                {
                    // .RollbackAll(), Camel's markRollbackOnly(): a rollback without an exception. The scope is disposed
                    // without Complete; the consumer sees the mark and does not acknowledge the message.
                    await TransactedActions.RollbackAll(exchange, _logger, ct).ConfigureAwait(false);
                    _logger?.LogInformation("Transaction rolled back: the route marked the exchange rollback-only.");
                    return;
                }

                if (exchange.Exception is { } failure && !exchange.ExceptionHandled)
                {
                    // The route left an unhandled failure on the exchange (OnException without Handled(true)) and
                    // returned normally, e.g. from a sub-route: that is a failed unit of work, not a commit. The scope
                    // is disposed without Complete, so the database rolls back together with the brokers.
                    await TransactedActions.RollbackAll(exchange, _logger, ct).ConfigureAwait(false);
                    _logger?.LogError(failure,
                        "Transaction rolled back: the exchange completed with an unhandled exception: {Message}",
                        failure.Message);
                    return;
                }

                scope.Complete();
            }   // the database transaction ends here, on dispose
        }
        catch (OperationCanceledException)
        {
            // Cancelled before the database committed: nothing was sent, roll the deferred actions back and re-throw.
            await TransactedActions.RollbackAll(exchange, _logger, ct).ConfigureAwait(false);
            _logger?.LogWarning("Transaction rolled back due to cancellation.");
            throw;
        }
        catch (Exception ex)
        {
            // The database did not commit, so no broker hears about the work: roll the deferred actions back.
            await TransactedActions.RollbackAll(exchange, _logger, ct).ConfigureAwait(false);
            // The block's work is undone, so an OnException ... Continued() handler must not replay the steps left in
            // it: routing picks up after the block, not inside it.
            Processors.ResumePoints.Forget(exchange, ex);
            _logger?.LogError(ex, "Transaction rolled back due to exception: {Message}", ex.Message);
            throw;
        }

        try
        {
            await TransactedActions.CommitAll(exchange, ct).ConfigureAwait(false);
            _logger?.LogDebug("Transaction committed successfully.");
        }
        catch (Exception ex)
        {
            // The work is in the database and cannot be taken back. Whatever is left unsent is rolled back and the
            // failure propagates, so the consumer does not acknowledge the message: the broker redelivers it and the
            // idempotent consumer keeps the second pass from doing the work twice.
            await TransactedActions.RollbackAll(exchange, _logger, ct).ConfigureAwait(false);
            _logger?.LogError(ex,
                "The database transaction committed, then a deferred transport action failed: {Message}. The work is " +
                "stored; the message is not acknowledged, so the broker will redeliver it.", ex.Message);
            throw;
        }
    }

    /// <summary>[Diag-TX-SCOPE] — best-effort RouteId lookup from the exchange.</summary>
    private static string GetRouteIdSafe(IExchange exchange)
    {
        try
        {
            if (exchange.Properties != null
                && exchange.Properties.TryGetValue("CamelRouteId", out var rid) && rid is string s)
                return s;
            return exchange.In?.Headers?.GetType().GetProperty("RouteId")?.GetValue(exchange.In.Headers) as string
                ?? "<unknown>";
        }
        catch { return "<error>"; }
    }
}

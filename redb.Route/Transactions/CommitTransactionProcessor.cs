using System.Transactions;
using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.Core;

namespace redb.Route.Transactions;

/// <summary>
/// Imperative transaction processor that commits the <see cref="TransactionScope"/>
/// previously opened by <see cref="BeginTransactionProcessor"/>.
/// Commits the database first and the deferred <see cref="ITransactedAction"/> instances after it, in the order
/// <c>.Transacted()</c> uses. Joined to an enclosing block, it leaves both to that block.
/// </summary>
public sealed class CommitTransactionProcessor : IProcessor
{
    private readonly ILogger? _logger;

    /// <summary>Creates a commit-transaction processor.</summary>
    /// <param name="logger">Optional logger.</param>
    public CommitTransactionProcessor(ILogger? logger = null)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task Process(IExchange exchange, CancellationToken ct = default)
    {
        if (!exchange.Properties.TryGetValue(BeginTransactionProcessor.ScopePropertyKey, out var raw) ||
            raw is not TransactionScope scope)
        {
            _logger?.LogWarning("CommitTransaction called but no active transaction scope found on exchange.");
            return;
        }

        if (exchange.IsRollbackOnly())
        {
            // The route marked the unit of work for rollback: committing it would undo the mark.
            await new RollbackTransactionProcessor(_logger).Process(exchange, ct).ConfigureAwait(false);
            return;
        }

        exchange.Properties.Remove(BeginTransactionProcessor.ScopePropertyKey);
        var ownsActions = BeginTransactionProcessor.TryTakeOwnActions(exchange, out var enclosing);
        try
        {
            try
            {
                // The database first. Joined to an enclosing block, this only votes for the commit: the enclosing block
                // commits the database and the sends.
                try { scope.Complete(); }
                finally { scope.Dispose(); }
            }
            catch (Exception ex) when (ownsActions)
            {
                // The database did not commit, so no broker hears about the work.
                await TransactedActions.RollbackAll(exchange, _logger, ct).ConfigureAwait(false);
                _logger?.LogError(ex, "Transaction rolled back: the database commit failed: {Message}", ex.Message);
                throw;
            }

            if (!ownsActions)
            {
                _logger?.LogDebug("Joined transaction completed imperatively; the enclosing block commits it.");
                return;
            }

            try
            {
                await TransactedActions.CommitAll(exchange, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // The work is in the database and cannot be taken back; what is left unsent is rolled back and the
                // failure propagates, so the incoming message is not acknowledged and is redelivered.
                await TransactedActions.RollbackAll(exchange, _logger, ct).ConfigureAwait(false);
                _logger?.LogError(ex,
                    "The database transaction committed, then a deferred transport action failed: {Message}. The work " +
                    "is stored; the message is not acknowledged, so the broker will redeliver it.", ex.Message);
                throw;
            }

            _logger?.LogDebug("Transaction committed imperatively.");
        }
        finally
        {
            if (ownsActions)
                TransactedActions.Close(exchange, enclosing);
        }
    }
}

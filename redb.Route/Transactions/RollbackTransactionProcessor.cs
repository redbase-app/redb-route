using System.Transactions;
using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;

namespace redb.Route.Transactions;

/// <summary>
/// Imperative transaction processor that rolls back the <see cref="TransactionScope"/>
/// previously opened by <see cref="BeginTransactionProcessor"/>.
/// Rolls back all deferred <see cref="ITransactedAction"/> instances, then disposes the scope
/// without calling <see cref="TransactionScope.Complete"/>. Joined to an enclosing block, it marks the enclosing
/// transaction for rollback, and the enclosing block rolls back the database and every send.
/// </summary>
public sealed class RollbackTransactionProcessor : IProcessor
{
    private readonly ILogger? _logger;

    /// <summary>Creates a rollback-transaction processor.</summary>
    /// <param name="logger">Optional logger.</param>
    public RollbackTransactionProcessor(ILogger? logger = null)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task Process(IExchange exchange, CancellationToken ct = default)
    {
        if (!exchange.Properties.TryGetValue(BeginTransactionProcessor.ScopePropertyKey, out var raw) ||
            raw is not TransactionScope scope)
        {
            _logger?.LogWarning("RollbackTransaction called but no active transaction scope found on exchange.");
            return;
        }

        exchange.Properties.Remove(BeginTransactionProcessor.ScopePropertyKey);
        var ownsActions = BeginTransactionProcessor.TryTakeOwnActions(exchange, out var enclosing);
        try
        {
            if (ownsActions)
                await TransactedActions.RollbackAll(exchange, _logger, ct).ConfigureAwait(false);

            // Dispose without Complete → automatic rollback
            scope.Dispose();
        }
        finally
        {
            if (ownsActions)
                TransactedActions.Close(exchange, enclosing);
        }

        _logger?.LogDebug("Transaction rolled back imperatively.");
    }
}

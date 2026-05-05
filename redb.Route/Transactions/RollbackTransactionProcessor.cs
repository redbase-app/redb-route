using System.Transactions;
using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;

namespace redb.Route.Transactions;

/// <summary>
/// Imperative transaction processor that rolls back the <see cref="TransactionScope"/>
/// previously opened by <see cref="BeginTransactionProcessor"/>.
/// Rolls back all deferred <see cref="ITransactedAction"/> instances, then disposes the scope
/// without calling <see cref="TransactionScope.Complete"/>.
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

        // Rollback all deferred transport actions
        var actions = TransactedProcessor.GetActionsPublic(exchange);
        if (actions is not null)
        {
            foreach (var kvp in actions)
            {
                try
                {
                    await kvp.Value.Rollback(ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Rollback failed for action '{ActionKey}'. Suppressing.", kvp.Key);
                }
            }
            actions.Clear();
        }

        // Dispose without Complete → automatic rollback
        scope.Dispose();
        exchange.Properties.Remove(BeginTransactionProcessor.ScopePropertyKey);

        _logger?.LogDebug("Transaction rolled back imperatively.");
    }
}

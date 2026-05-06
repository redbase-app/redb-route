using System.Transactions;
using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;

namespace redb.Route.Transactions;

/// <summary>
/// Imperative transaction processor that commits the <see cref="TransactionScope"/>
/// previously opened by <see cref="BeginTransactionProcessor"/>.
/// Commits all deferred <see cref="ITransactedAction"/> instances, then completes the scope.
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

        // Commit all deferred transport actions (RabbitMQ, Kafka, etc.)
        var actions = TransactedProcessor.GetActionsPublic(exchange);
        if (actions is not null)
        {
            foreach (var kvp in actions)
            {
                await kvp.Value.Commit(ct).ConfigureAwait(false);
            }
            actions.Clear();
        }

        scope.Complete();
        scope.Dispose();
        exchange.Properties.Remove(BeginTransactionProcessor.ScopePropertyKey);

        _logger?.LogDebug("Transaction committed imperatively.");
    }
}

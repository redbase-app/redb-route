using System.Collections.Concurrent;
using System.Transactions;
using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;

namespace redb.Route.Transactions;

/// <summary>
/// Imperative transaction processor that opens a <see cref="TransactionScope"/>
/// and stores it on the exchange for later commit/rollback.
/// <para>
/// Use <see cref="CommitTransactionProcessor"/> or <see cref="RollbackTransactionProcessor"/>
/// to explicitly close the scope. If neither is called, the scope will be
/// disposed (rolled back) when the exchange completes.
/// </para>
/// </summary>
public sealed class BeginTransactionProcessor : IProcessor
{
    /// <summary>Well-known exchange property key for the active <see cref="TransactionScope"/>.</summary>
    internal const string ScopePropertyKey = "TRANSACTION_SCOPE";

    private readonly TransactionPolicy _policy;
    private readonly ILogger? _logger;

    /// <summary>Creates a begin-transaction processor.</summary>
    /// <param name="policy">Transaction policy (scope option, timeout, isolation level).</param>
    /// <param name="logger">Optional logger.</param>
    public BeginTransactionProcessor(TransactionPolicy? policy = null, ILogger? logger = null)
    {
        _policy = policy ?? TransactionPolicy.Default;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task Process(IExchange exchange, CancellationToken ct = default)
    {
        // Ensure TRANSACT_ACTION dictionary exists for transports
        if (!exchange.Properties.ContainsKey(TransactedProcessor.TransactActionPropertyKey))
        {
            exchange.Properties[TransactedProcessor.TransactActionPropertyKey] =
                new ConcurrentDictionary<string, ITransactedAction>(StringComparer.OrdinalIgnoreCase);
        }

        var scope = _policy.CreateScope();
        exchange.Properties[ScopePropertyKey] = scope;

        _logger?.LogDebug(
            "Transaction started imperatively (ScopeOption={ScopeOption}, IsolationLevel={IsolationLevel}).",
            _policy.ScopeOption, _policy.IsolationLevel);

        return Task.CompletedTask;
    }
}

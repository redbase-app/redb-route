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

    /// <summary>
    /// Set when the block is a unit of work of its own: holds the enclosing block's set of deferred actions (or a marker
    /// for none), which its commit or rollback puts back. Absent when the block joined an enclosing one.
    /// </summary>
    internal const string EnclosingActionsPropertyKey = "TRANSACTION_ENCLOSING_ACTIONS";

    private static readonly object NoEnclosingActions = new();

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
        // The same rules as .Transacted(): a Required block inside a running transaction joins it and leaves its sends to
        // the enclosing block; otherwise the block keeps a set of its own until CommitTransaction or
        // RollbackTransaction, which puts the enclosing set back, so a transacted send after the block is refused
        // instead of deferred into a set nobody commits.
        var joins = TransactedActions.JoinsEnclosingBlock(exchange, _policy);

        var scope = _policy.CreateScope();
        exchange.Properties[ScopePropertyKey] = scope;
        if (!joins)
            exchange.Properties[EnclosingActionsPropertyKey] = (object?)TransactedActions.Open(exchange) ?? NoEnclosingActions;

        _logger?.LogDebug(
            "Transaction started imperatively (ScopeOption={ScopeOption}, IsolationLevel={IsolationLevel}, Joined={Joined}).",
            _policy.ScopeOption, _policy.IsolationLevel, joins);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Takes the block's own set off the exchange. Returns <c>true</c>, with the enclosing block's set or <c>null</c>,
    /// when the block was a unit of work of its own; <c>false</c> when it joined an enclosing block.
    /// </summary>
    internal static bool TryTakeOwnActions(IExchange exchange, out ConcurrentDictionary<string, ITransactedAction>? enclosing)
    {
        enclosing = null;
        if (!exchange.Properties.TryGetValue(EnclosingActionsPropertyKey, out var raw))
            return false;

        exchange.Properties.Remove(EnclosingActionsPropertyKey);
        enclosing = raw as ConcurrentDictionary<string, ITransactedAction>;
        return true;
    }
}

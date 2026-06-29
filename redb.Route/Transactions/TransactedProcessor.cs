using System.Collections.Concurrent;
using System.Transactions;
using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;

namespace redb.Route.Transactions;

/// <summary>
/// Wraps an inner processor in a <see cref="TransactionScope"/> and manages
/// deferred <see cref="ITransactedAction"/> commit/rollback.
/// <para>
/// Transports (Kafka, Redis, RabbitMQ, etc.) register <see cref="ITransactedAction"/> instances
/// in <c>exchange.Properties["TRANSACT_ACTION"]</c> as a
/// <see cref="ConcurrentDictionary{TKey,TValue}"/> keyed by a per-message-unique name
/// (e.g. <c>kafka-send-{guid}</c>, <c>rabbitmq-ack-{deliveryTag}</c>, <c>asb-ack-{sequence}</c> —
/// never the endpoint URI, so parallel fan-out branches to the same endpoint never collide).
/// On success all actions are committed; on failure they are rolled back.
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
        // Ensure the exchange has a TRANSACT_ACTION dictionary so transports can register
        if (!exchange.Properties.ContainsKey(TransactActionPropertyKey))
        {
            exchange.Properties[TransactActionPropertyKey] =
                new ConcurrentDictionary<string, ITransactedAction>(StringComparer.OrdinalIgnoreCase);
        }

        using var scope = _policy.CreateScope();

        // var diagScopeId = System.Transactions.Transaction.Current?.TransactionInformation.LocalIdentifier ?? "<null>";
        // System.Console.WriteLine(
        //     $"[Diag-TX-SCOPE] CREATED routeId={GetRouteIdSafe(exchange)} scopeId={diagScopeId} " +
        //     $"thread={System.Environment.CurrentManagedThreadId}");

        _logger?.LogDebug(
            "Transaction started (ScopeOption={ScopeOption}, IsolationLevel={IsolationLevel}, Timeout={Timeout}).",
            _policy.ScopeOption, _policy.IsolationLevel, _policy.Timeout);

        try
        {
            // System.Console.WriteLine(
            //     $"[Diag-TX-SCOPE] PRE-INNER routeId={GetRouteIdSafe(exchange)} scopeId={diagScopeId} " +
            //     $"ambient={(System.Transactions.Transaction.Current?.TransactionInformation.LocalIdentifier ?? "<null>")} " +
            //     $"thread={System.Environment.CurrentManagedThreadId}");
            await _inner.Process(exchange, ct).ConfigureAwait(false);
            // System.Console.WriteLine(
            //     $"[Diag-TX-SCOPE] POST-INNER routeId={GetRouteIdSafe(exchange)} scopeId={diagScopeId} " +
            //     $"ambient={(System.Transactions.Transaction.Current?.TransactionInformation.LocalIdentifier ?? "<null>")} " +
            //     $"thread={System.Environment.CurrentManagedThreadId}");

            // Commit all deferred transport actions
            await CommitActions(exchange, ct).ConfigureAwait(false);

            scope.Complete();

            _logger?.LogDebug("Transaction committed successfully.");
        }
        catch (OperationCanceledException)
        {
            // Cancellation — rollback deferred actions, re-throw
            await RollbackActions(exchange, ct).ConfigureAwait(false);
            _logger?.LogWarning("Transaction rolled back due to cancellation.");
            throw;
        }
        catch (Exception ex)
        {
            // Failure — rollback deferred actions, re-throw
            await RollbackActions(exchange, ct).ConfigureAwait(false);
            _logger?.LogError(ex, "Transaction rolled back due to exception: {Message}", ex.Message);
            throw;
        }
    }

    /// <summary>
    /// Commits all <see cref="ITransactedAction"/> instances registered in exchange properties.
    /// </summary>
    private static async Task CommitActions(IExchange exchange, CancellationToken ct)
    {
        var actions = GetActions(exchange);
        if (actions is null || actions.IsEmpty)
            return;

        foreach (var kvp in actions)
        {
            await kvp.Value.Commit(ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Rolls back all <see cref="ITransactedAction"/> instances registered in exchange properties.
    /// Rollback failures are logged but suppressed so the original exception propagates.
    /// </summary>
    private async Task RollbackActions(IExchange exchange, CancellationToken ct)
    {
        var actions = GetActions(exchange);
        if (actions is null || actions.IsEmpty)
            return;

        foreach (var kvp in actions)
        {
            try
            {
                await kvp.Value.Rollback(ct).ConfigureAwait(false);
            }
            catch (Exception rollbackEx)
            {
                _logger?.LogWarning(rollbackEx,
                    "Rollback failed for transacted action '{ActionKey}'. Suppressing to preserve original exception.",
                    kvp.Key);
            }
        }
    }

    /// <summary>
    /// Retrieves the <see cref="ConcurrentDictionary{TKey,TValue}"/> of deferred actions from the exchange.
    /// </summary>
    private static ConcurrentDictionary<string, ITransactedAction>? GetActions(IExchange exchange)
    {
        return exchange.Properties.TryGetValue(TransactActionPropertyKey, out var raw) &&
               raw is ConcurrentDictionary<string, ITransactedAction> dict
            ? dict
            : null;
    }

    /// <summary>
    /// Retrieves deferred actions from the exchange. Used by imperative commit/rollback processors.
    /// </summary>
    internal static ConcurrentDictionary<string, ITransactedAction>? GetActionsPublic(IExchange exchange)
        => GetActions(exchange);

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

using System.Collections.Concurrent;
using System.Transactions;
using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;

namespace redb.Route.Transactions;

/// <summary>
/// Registration of deferred transport actions: an outgoing send or write that a transacted endpoint
/// (<c>transacted=true</c>) postpones until the enclosing <c>.Transacted()</c> block has committed the database.
/// <para>
/// An action is accepted only inside an active block. Outside one — a transacted step on a route without
/// <c>.Transacted()</c>, or after the block has ended — nothing would ever commit it and the write would silently
/// disappear, so registration fails instead.
/// </para>
/// </summary>
public static class TransactedActions
{
    /// <summary>Whether <paramref name="exchange"/> is inside an active transacted block.</summary>
    public static bool IsActive(IExchange exchange)
    {
        ArgumentNullException.ThrowIfNull(exchange);
        return Find(exchange) is not null;
    }

    /// <summary>
    /// Whether a producer defers its send to the enclosing transacted block, by its <c>transacted</c> parameter.
    /// Unset, the send follows the block, as in Camel: deferred inside <c>.Transacted()</c>, sent at once outside it.
    /// <c>false</c> sends at once even inside a block. <c>true</c> always defers, so outside a block
    /// <see cref="Register"/> refuses the send instead of losing it.
    /// </summary>
    /// <param name="exchange">The exchange the producer is processing.</param>
    /// <param name="transacted">The producer's <c>transacted</c> parameter; <c>null</c> when the route did not set it.</param>
    public static bool Defers(IExchange exchange, bool? transacted) => transacted ?? IsActive(exchange);

    /// <summary>
    /// Defers <paramref name="action"/> to the enclosing transacted block, which commits it after the database or rolls
    /// it back on failure. Throws <see cref="InvalidOperationException"/> when the exchange is not inside one.
    /// </summary>
    /// <param name="exchange">The exchange the step is processing.</param>
    /// <param name="key">A key unique within the block.</param>
    /// <param name="action">The deferred action.</param>
    /// <param name="endpoint">A short, secret-free description of the endpoint for the error message.</param>
    public static void Register(IExchange exchange, string key, ITransactedAction action, string endpoint)
    {
        ArgumentNullException.ThrowIfNull(exchange);
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(action);

        if (Find(exchange) is not { } actions)
            throw NotInBlock(endpoint);

        if (actions is BlockActions block)
            block.Put(key, action);
        else
            actions[key] = action;
    }

    /// <summary>
    /// Defers a prepared send to the enclosing transacted block: <paramref name="send"/> runs once the database has
    /// committed and is dropped on rollback. Build everything the send needs from the exchange before registering it;
    /// the delegate runs after the rest of the block. Throws, as <see cref="Register"/> does, outside a block.
    /// </summary>
    /// <param name="exchange">The exchange the step is processing.</param>
    /// <param name="key">A key unique within the block.</param>
    /// <param name="send">The send, run on commit.</param>
    /// <param name="endpoint">A short, secret-free description of the endpoint for the error message.</param>
    public static void RegisterSend(IExchange exchange, string key, Func<CancellationToken, Task> send, string endpoint)
    {
        ArgumentNullException.ThrowIfNull(send);
        Register(exchange, key, new DeferredSend(send), endpoint);
    }

    /// <summary>
    /// The batch a producer keeps in the enclosing block under <paramref name="key"/>: the one it registered earlier in the
    /// block, or a new one from <paramref name="create"/>. A producer that commits all its deferred sends of a block in one
    /// native transaction of its broker collects them in such a batch, which keeps the place of the first send in the
    /// block's commit order. Throws, as <see cref="Register"/> does, outside a block.
    /// </summary>
    /// <param name="exchange">The exchange the step is processing.</param>
    /// <param name="key">The producer's key, the same for every send it defers in a block.</param>
    /// <param name="create">Creates the batch for the first send of the block.</param>
    /// <param name="endpoint">A short, secret-free description of the endpoint for the error message.</param>
    public static TBatch JoinBatch<TBatch>(IExchange exchange, string key, Func<TBatch> create, string endpoint)
        where TBatch : class, ITransactedAction
    {
        ArgumentNullException.ThrowIfNull(exchange);
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(create);

        if (Find(exchange) is not { } actions)
            throw NotInBlock(endpoint);

        var action = actions is BlockActions block ? block.GetOrPut(key, create) : actions.GetOrAdd(key, _ => create());
        return action as TBatch
            ?? throw new InvalidOperationException($"'{key}' holds a {action.GetType().Name}, not the batch of '{endpoint}'.");
    }

    private static InvalidOperationException NotInBlock(string endpoint) => new(
        $"'{endpoint}' is transacted: its write is deferred until the enclosing .Transacted() block commits, " +
        "and this step is not inside one, so nothing would ever commit the write. Put the step inside " +
        ".Transacted() ... .End(), or drop transacted=true to write at once.");

    private sealed class DeferredSend(Func<CancellationToken, Task> send) : ITransactedAction
    {
        public Task Commit(CancellationToken ct = default) => send(ct);

        public Task Rollback(CancellationToken ct = default) => Task.CompletedTask;
    }

    /// <summary>
    /// Whether a block with <paramref name="policy"/> joins the block it runs in: a Required or Mandatory block inside a
    /// running transaction is part of that unit of work, and its sends leave with the enclosing commit.
    /// </summary>
    internal static bool JoinsEnclosingBlock(IExchange exchange, TransactionPolicy policy) =>
        policy.JoinsRunningTransaction && Transaction.Current is not null && IsActive(exchange);

    /// <summary>
    /// The set of the block the exchange is in, or <c>null</c> outside a block. A set whose block has ended does not
    /// count: a copy of the exchange (a clone kept by an aggregator, a resequencer, a late hand-off) still holds it, and
    /// a send registered there would be committed by nobody.
    /// </summary>
    internal static ConcurrentDictionary<string, ITransactedAction>? Find(IExchange exchange) =>
        exchange.Properties.TryGetValue(TransactedProcessor.TransactActionPropertyKey, out var raw)
        && raw is ConcurrentDictionary<string, ITransactedAction> set
        && set is not BlockActions { IsClosed: true }
            ? set
            : null;

    /// <summary>Opens an empty set for a block of its own and returns the enclosing block's set, if any.</summary>
    internal static ConcurrentDictionary<string, ITransactedAction>? Open(IExchange exchange)
    {
        var enclosing = Find(exchange);
        exchange.Properties[TransactedProcessor.TransactActionPropertyKey] = new BlockActions();
        return enclosing;
    }

    /// <summary>Closes the block's set for good and puts back the enclosing block's set, if there was one.</summary>
    internal static void Close(IExchange exchange, ConcurrentDictionary<string, ITransactedAction>? enclosing)
    {
        if (exchange.Properties.TryGetValue(TransactedProcessor.TransactActionPropertyKey, out var raw) &&
            raw is BlockActions own)
        {
            own.Close();
        }

        if (enclosing is null)
            exchange.Properties.Remove(TransactedProcessor.TransactActionPropertyKey);
        else
            exchange.Properties[TransactedProcessor.TransactActionPropertyKey] = enclosing;
    }

    /// <summary>
    /// Detaches a copy of the exchange that is handed over asynchronously (<c>seda:</c>, <c>vm:</c>, an InOnly
    /// <c>.Threads()</c> worker) from the sending block. The copy runs later, on another flow, as a unit of work of its
    /// own, as it does in Camel, where a transaction belongs to the thread that opened it.
    /// </summary>
    internal static void DetachFromBlock(IExchange copy) =>
        copy.Properties.Remove(TransactedProcessor.TransactActionPropertyKey);

    /// <summary>
    /// The deferred actions of one block, in the order the route registered them (two sends to one queue must not
    /// arrive swapped); closed for good when the block ends.
    /// </summary>
    private sealed class BlockActions() : ConcurrentDictionary<string, ITransactedAction>(StringComparer.OrdinalIgnoreCase)
    {
        private readonly ConcurrentQueue<string> _order = new();
        private volatile bool _closed;

        public bool IsClosed => _closed;

        public void Close() => _closed = true;

        /// <summary>Registers <paramref name="action"/> under <paramref name="key"/>; a key seen before keeps its place.</summary>
        public void Put(string key, ITransactedAction action)
        {
            if (TryAdd(key, action))
                _order.Enqueue(key);
            else
                this[key] = action;
        }

        /// <summary>The action under <paramref name="key"/>, registered by <paramref name="create"/> the first time.</summary>
        public ITransactedAction GetOrPut(string key, Func<ITransactedAction> create)
        {
            if (TryGetValue(key, out var existing))
                return existing;
            var created = create();
            Put(key, created);
            return created;
        }

        /// <summary>
        /// The actions still in the set, in registration order. An action put straight into the dictionary (code that
        /// writes the TRANSACT_ACTION property itself) has no recorded place and follows the others, so none is skipped.
        /// </summary>
        public IEnumerable<KeyValuePair<string, ITransactedAction>> InOrder()
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var key in _order)
            {
                if (seen.Add(key) && TryGetValue(key, out var action))
                    yield return new KeyValuePair<string, ITransactedAction>(key, action);
            }

            foreach (var kvp in this)
            {
                if (!seen.Contains(kvp.Key))
                    yield return kvp;
            }
        }
    }

    /// <summary>The set's actions in registration order (a set the block did not create has none to keep).</summary>
    private static IEnumerable<KeyValuePair<string, ITransactedAction>> InOrder(ConcurrentDictionary<string, ITransactedAction> actions) =>
        actions is BlockActions block ? block.InOrder() : actions;

    /// <summary>
    /// Commits the block's deferred actions. Called once the database transaction has closed. A committed action is
    /// taken out of the set, so a later attempt (Retry outside the transaction) never commits it a second time.
    /// </summary>
    internal static async Task CommitAll(IExchange exchange, CancellationToken ct)
    {
        var actions = Find(exchange);
        if (actions is null || actions.IsEmpty)
            return;

        // The block's own transaction is closed, but an enclosing block's may still be current (a RequiresNew block
        // inside a Required one). A deferred send belongs to no transaction, and a client that enlists in the ambient
        // one on its own (AMQP, Azure Service Bus) must not join the enclosing block's.
        using var noTransaction = new TransactionScope(TransactionScopeOption.Suppress, TransactionScopeAsyncFlowOption.Enabled);
        foreach (var kvp in InOrder(actions).ToList())
        {
            await kvp.Value.Commit(ct).ConfigureAwait(false);
            actions.TryRemove(kvp.Key, out _);
        }
    }

    /// <summary>
    /// Rolls back the block's deferred actions. A failed rollback is logged and does not hide the failure that caused
    /// it; rolled back or not, every action leaves the set with the attempt that registered it.
    /// </summary>
    internal static async Task RollbackAll(IExchange exchange, ILogger? logger, CancellationToken ct)
    {
        var actions = Find(exchange);
        if (actions is null || actions.IsEmpty)
            return;

        foreach (var kvp in InOrder(actions).ToList())
        {
            try
            {
                await kvp.Value.Rollback(ct).ConfigureAwait(false);
            }
            catch (Exception rollbackEx)
            {
                logger?.LogWarning(rollbackEx,
                    "Rollback failed for transacted action '{ActionKey}'. Suppressing to preserve original exception.",
                    kvp.Key);
            }
            finally
            {
                actions.TryRemove(kvp.Key, out _);
            }
        }
    }
}

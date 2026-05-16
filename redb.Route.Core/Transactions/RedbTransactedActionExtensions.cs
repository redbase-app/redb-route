using System.Collections.Concurrent;
using redb.Core;
using redb.Route.Abstractions;
using redb.Route.RedbCore.Extensions;
using redb.Route.Transactions;

namespace redb.Route.RedbCore.Transactions;

/// <summary>
/// DSL extensions for opening an explicit <c>redb</c> transaction inside a route pipeline
/// and enrolling it in the standard <see cref="TransactedProcessor"/> commit/rollback flow.
/// <para>
/// Pairs with the existing <c>.Transacted(...)</c> / <c>.BeginTransaction(...)</c> /
/// <c>.CommitTransaction()</c> / <c>.RollbackTransaction()</c> primitives:
/// when a <c>RedbTransactedAction</c> is registered in <c>exchange.Properties["TRANSACT_ACTION"]</c>,
/// it is committed or rolled back together with all other transports at the standard
/// <see cref="TransactedProcessor"/> boundary.
/// </para>
/// <para>
/// <b>Resolution model:</b> the underlying <see cref="IRedbService"/> is resolved through
/// <see cref="RedbRouteExtensions.GetRedbService(redb.Route.Abstractions.IRouteContext, string, IExchange?)"/>,
/// so anonymous singletons, named singletons, and named per-exchange scoped factories all work
/// (the same model used by <c>ProcessWithRedb(name, ...)</c>).
/// </para>
/// </summary>
public static class RedbTransactedActionExtensions
{
    /// <summary>
    /// Property-key prefix used when registering a <see cref="RedbTransactedAction"/> in
    /// <c>exchange.Properties["TRANSACT_ACTION"]</c>. The full key is <c>"redb:{name}"</c>
    /// (or <c>"redb:"</c> for the default unnamed instance).
    /// </summary>
    public const string ActionKeyPrefix = "redb:";

    /// <summary>
    /// Opens an explicit <see cref="IRedbTransaction"/> on the default <see cref="IRedbService"/>
    /// and enrolls it as an <see cref="ITransactedAction"/> in the exchange so that the
    /// surrounding <see cref="TransactedProcessor"/> (or imperative <c>Commit/RollbackTransaction</c>)
    /// will close it.
    /// </summary>
    public static IRouteDefinition BeginRedbTransaction(this IRouteDefinition route)
        => BeginRedbTransactionCore(route, name: null);

    /// <summary>
    /// Opens an explicit <see cref="IRedbTransaction"/> on a named <see cref="IRedbService"/>
    /// (per-exchange scoped when a factory is registered, see
    /// <see cref="RedbRouteExtensions.GetRedbService(redb.Route.Abstractions.IRouteContext, string, IExchange?)"/>)
    /// and enrolls it as an <see cref="ITransactedAction"/> in the exchange.
    /// </summary>
    /// <param name="route">Route definition.</param>
    /// <param name="name">Named redb instance (e.g. <c>"orders-db"</c>).</param>
    public static IRouteDefinition BeginRedbTransaction(this IRouteDefinition route, string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        return BeginRedbTransactionCore(route, name);
    }

    private static IRouteDefinition BeginRedbTransactionCore(IRouteDefinition route, string? name)
    {
        ArgumentNullException.ThrowIfNull(route);

        return route.Process(async (exchange, ct) =>
        {
            var context = route.GetContext()
                ?? throw new InvalidOperationException(
                    "RouteContext is not available. Ensure the route builder has been configured.");

            var redb = string.IsNullOrEmpty(name)
                ? context.GetRedbService()
                : context.GetRedbService(name!, exchange);

            var actionKey = ActionKeyPrefix + (name ?? string.Empty);

            // Idempotency: if a redb tx with this key is already enrolled in this exchange,
            // do not open a second one (Required-like semantics on the redb side).
            var actions = GetOrCreateActionsBag(exchange);
            if (actions.ContainsKey(actionKey))
                return;

            var tx = await redb.Context.BeginTransactionAsync().ConfigureAwait(false);
            actions[actionKey] = new RedbTransactedAction(tx);
        });
    }

    private static ConcurrentDictionary<string, ITransactedAction> GetOrCreateActionsBag(IExchange exchange)
    {
        if (exchange.Properties.TryGetValue(TransactedProcessor.TransactActionPropertyKey, out var raw)
            && raw is ConcurrentDictionary<string, ITransactedAction> existing)
        {
            return existing;
        }

        var bag = new ConcurrentDictionary<string, ITransactedAction>(StringComparer.OrdinalIgnoreCase);
        exchange.Properties[TransactedProcessor.TransactActionPropertyKey] = bag;
        return bag;
    }
}

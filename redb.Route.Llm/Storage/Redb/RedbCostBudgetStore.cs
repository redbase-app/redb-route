using redb.Core;
using redb.Core.Exceptions;
using redb.Core.Models.Entities;
using redb.Route.Abstractions;
using redb.Route.Llm.Engine.Governance;
using redb.Route.Llm.Engine.Storage;
using redb.Route.Llm.Storage.Redb.Schemas;
using redb.Route.RedbCore;
using redb.Route.RedbCore.Extensions;

namespace redb.Route.Llm.Storage.Redb;

/// <summary>
/// REDB-backed <see cref="ICostBudgetStore"/>. One <see cref="CostBudgetProps"/>
/// row per conversation, keyed on the conversation id stored in
/// <c>_objects.value_string</c> (partial index on PostgreSQL/SQLite; MSSQL cannot
/// index NVARCHAR(MAX)) and, normalized, in <c>_objects._value_unique</c> — the
/// per-scheme unique index guarantees ONE budget row per conversation even under
/// concurrent first writes.
/// <para>
/// <see cref="AddAsync"/> uses the framework's pessimistic-locking primitives
/// (<c>redb.Context.ExecuteAtomicAsync</c> + <c>redb.LockForUpdateAsync</c>) so
/// concurrent updates from multiple processes / nodes serialise on the row
/// instead of trampling each other. No raw SQL.
/// </para>
/// <para>
/// The store does not own an <see cref="IRedbService"/> instance — each call
/// resolves one through <c>IRouteContext.GetRedbService(name, exchange)</c>,
/// which honours the per-exchange scope cache. The redb name is read from
/// <c>exchange.Properties[LlmKeys.RedbName]</c> (set by the LLM endpoint URI),
/// falling back to the constructor-supplied default name and then to the host's
/// default unnamed instance.
/// </para>
/// </summary>
public sealed class RedbCostBudgetStore : ICostBudgetStore
{
    private readonly IRouteContext _context;
    private readonly string? _defaultRedbName;

    /// <summary>Creates the store. Scheme is synced by the host's redb.InitializeAsync().</summary>
    public RedbCostBudgetStore(IRouteContext context, string? defaultRedbName = null)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _defaultRedbName = defaultRedbName;
    }

    private IRedbService Resolve(IExchange? exchange)
    {
        var name = _defaultRedbName;
        if (exchange is not null
            && exchange.Properties.TryGetValue(LlmKeys.RedbName, out var raw)
            && raw is string s && s.Length > 0)
            name = s;
        return _context.GetRedbService(name ?? string.Empty, exchange);
    }

    /// <inheritdoc />
    public async ValueTask<AgentUsage> GetUsageAsync(string conversationId, IExchange? exchange = null, CancellationToken ct = default)
    {
        var redb = Resolve(exchange);
        var row = await LoadAsync(redb, conversationId).ConfigureAwait(false);
        return row is null
            ? AgentUsage.Zero
            : new AgentUsage((int)row.Props.InputTokens, (int)row.Props.OutputTokens, row.Props.CostUsd);
    }

    /// <inheritdoc />
    public async ValueTask<AgentUsage> AddAsync(string conversationId, AgentUsage delta, IExchange? exchange = null, CancellationToken ct = default)
    {
        var redb = Resolve(exchange);

        // Two passes at most: a row soft-deleted mid-flight (a concurrent ResetAsync —
        // the documented daily/monthly counter roll) sends the loop back to the
        // first-write branch, which starts the new period with this delta instead of
        // throwing it away.
        for (var attempt = 0; ; attempt++)
        {
            // First write happens OUTSIDE the atomic block: the row carries the
            // conversation key in _value_unique, so of two concurrent first writers the
            // per-scheme unique index lets exactly one insert through. The row-lock
            // alone never covered creation: both writers used to insert, splitting the
            // budget across two rows. (The core now savepoints saves under a caller's
            // transaction, so a violation no longer dooms it — the insert stays outside
            // our own atomic block anyway: the common first write needs no transaction
            // at all, and the loser recovers on a clean connection state.)
            var existing = await LoadAsync(redb, conversationId).ConfigureAwait(false);
            if (existing is null)
            {
                var fresh = new RedbObject<CostBudgetProps>
                {
                    value_string = conversationId,
                    ValueUnique = RedbUniqueKey.Normalize(conversationId),
                    Props = new CostBudgetProps
                    {
                        InputTokens = delta.InputTokens,
                        OutputTokens = delta.OutputTokens,
                        CostUsd = delta.CostUsd,
                        UpdatedAtUtc = DateTimeOffset.UtcNow
                    }
                };
                try
                {
                    await redb.SaveAsync(fresh).ConfigureAwait(false);
                    return new AgentUsage((int)fresh.Props.InputTokens, (int)fresh.Props.OutputTokens, fresh.Props.CostUsd);
                }
                catch (RedbUniqueViolationException)
                {
                    // Lost the first-write race — the winner's row exists now; fall
                    // through to the ordinary locked increment onto it.
                }
            }

            var updated = await TryIncrementAsync(redb, conversationId, existing?.id, delta).ConfigureAwait(false);
            if (updated is not null) return updated.Value;

            // The row vanished between resolve and increment (concurrent ResetAsync).
            if (attempt == 1)
                throw new InvalidOperationException(
                    $"Cost-budget row for conversation '{conversationId}' keeps disappearing mid-increment.");
        }
    }

    /// <summary>
    /// Locked increment of the existing budget row; <c>null</c> when the row vanished
    /// (the caller restarts from the first-write branch). <paramref name="knownId"/>
    /// carries the pre-check's resolve so the common path pays no extra lookup.
    /// </summary>
    private static async Task<AgentUsage?> TryIncrementAsync(IRedbService redb, string conversationId, long? knownId, AgentUsage delta)
    {
        AgentUsage? updated = null;
        await redb.Context.ExecuteAtomicAsync(async () =>
        {
            long id;
            if (knownId is { } known)
            {
                id = known;
            }
            else
            {
                // Arrived from the lost-insert race: resolve the winner's row.
                var resolved = await LoadByKeyAsync(redb, conversationId).ConfigureAwait(false);
                if (resolved is null) return; // vanished — caller retries
                id = resolved.id;
            }

            await redb.LockForUpdateAsync(id).ConfigureAwait(false);

            // Re-read BY QUERY after the lock, not redb.LoadAsync. The core has since
            // stopped serving the zero-DB cache shortcut inside a transaction
            // (docs/BUG_REDB_CORE_CACHE_AND_AMBIENT_TX.md п.1), but the query path
            // guarantees a fresh row read regardless of cache mode or core version —
            // and a stale pre-lock copy here would lose another node's increments,
            // the exact defect this lock exists to prevent.
            var row = await redb.Query<CostBudgetProps>()
                .WhereRedb(x => x.Id == id)
                .FirstOrDefaultAsync()
                .ConfigureAwait(false);
            if (row is null) return; // vanished after lock — caller retries

            row.Props.InputTokens += delta.InputTokens;
            row.Props.OutputTokens += delta.OutputTokens;
            row.Props.CostUsd += delta.CostUsd;
            row.Props.UpdatedAtUtc = DateTimeOffset.UtcNow;
            row.date_modify = row.Props.UpdatedAtUtc;
            await redb.SaveAsync(row).ConfigureAwait(false);

            updated = new AgentUsage((int)row.Props.InputTokens, (int)row.Props.OutputTokens, row.Props.CostUsd);
        }).ConfigureAwait(false);

        return updated;
    }

    /// <inheritdoc />
    public async ValueTask ResetAsync(string conversationId, IExchange? exchange = null, CancellationToken ct = default)
    {
        var redb = Resolve(exchange);
        var row = await LoadAsync(redb, conversationId).ConfigureAwait(false);
        if (row is not null) await redb.SoftDeleteAsync([row]).ConfigureAwait(false);
    }

    private static Task<RedbObject<CostBudgetProps>?> LoadAsync(IRedbService redb, string conversationId)
        => redb.Query<CostBudgetProps>()
            .WhereRedb(x => x.ValueUnique == RedbUniqueKey.Normalize(conversationId))
            .FirstOrDefaultAsync();

    /// <summary>
    /// Row lookup for the increment path: by the readable <c>value_string</c> first
    /// (legacy rows carry only that), by the unique key second — the loser of a
    /// creation race resolves the winner through the key that collided.
    /// </summary>
    private static async Task<RedbObject<CostBudgetProps>?> LoadByKeyAsync(IRedbService redb, string conversationId)
    {
        var row = await LoadAsync(redb, conversationId).ConfigureAwait(false);
        if (row is not null) return row;

        var uniqueKey = RedbUniqueKey.Normalize(conversationId);
        return await redb.Query<CostBudgetProps>()
            .WhereRedb(x => x.ValueUnique == uniqueKey)
            .FirstOrDefaultAsync()
            .ConfigureAwait(false);
    }
}

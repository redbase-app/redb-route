using redb.Core;
using redb.Core.Models.Entities;
using redb.Route.Abstractions;
using redb.Route.Llm.Engine.Governance;
using redb.Route.Llm.Engine.Storage;
using redb.Route.Llm.Storage.Redb.Schemas;
using redb.Route.RedbCore.Extensions;

namespace redb.Route.Llm.Storage.Redb;

/// <summary>
/// REDB-backed <see cref="ICostBudgetStore"/>. One <see cref="CostBudgetProps"/>
/// row per conversation, keyed on the conversation id stored in the indexed
/// <c>_objects.value_string</c> column.
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

        AgentUsage updated = AgentUsage.Zero;
        await redb.Context.ExecuteAtomicAsync(async () =>
        {
            // Find first OUTSIDE the lock (no row id yet). The transaction
            // wrapping ExecuteAtomicAsync ensures the read is consistent with
            // the subsequent LockForUpdate / Save.
            var row = await LoadAsync(redb, conversationId).ConfigureAwait(false);

            if (row is null)
            {
                // First write for this conversation. SaveAsync's batch path
                // takes its own row lock for the insert.
                row = new RedbObject<CostBudgetProps>
                {
                    value_string = conversationId,
                    Props = new CostBudgetProps
                    {
                        InputTokens = delta.InputTokens,
                        OutputTokens = delta.OutputTokens,
                        CostUsd = delta.CostUsd,
                        UpdatedAtUtc = DateTimeOffset.UtcNow
                    }
                };
                await redb.SaveAsync(row).ConfigureAwait(false);
            }
            else
            {
                // SELECT ... FOR UPDATE on the existing row, then re-read to
                // pick up any concurrent updates that landed before the lock.
                await redb.LockForUpdateAsync(row.id).ConfigureAwait(false);
                row = await LoadAsync(redb, conversationId).ConfigureAwait(false)
                      ?? throw new InvalidOperationException(
                          $"Cost-budget row for conversation '{conversationId}' disappeared after lock.");

                row.Props.InputTokens += delta.InputTokens;
                row.Props.OutputTokens += delta.OutputTokens;
                row.Props.CostUsd += delta.CostUsd;
                row.Props.UpdatedAtUtc = DateTimeOffset.UtcNow;
                row.date_modify = row.Props.UpdatedAtUtc;
                await redb.SaveAsync(row).ConfigureAwait(false);
            }

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
            .WhereRedb(x => x.ValueString == conversationId)
            .FirstOrDefaultAsync();
}

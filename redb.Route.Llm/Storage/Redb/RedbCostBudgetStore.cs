using Microsoft.Extensions.DependencyInjection;
using redb.Core;
using redb.Core.Models.Entities;
using redb.Route.Llm.Engine.Governance;
using redb.Route.Llm.Engine.Storage;
using redb.Route.Llm.Storage.Redb.Schemas;

namespace redb.Route.Llm.Storage.Redb;

/// <summary>
/// REDB-backed <see cref="ICostBudgetStore"/>. One <see cref="CostBudgetProps"/>
/// row per conversation, keyed on the conversation id stored in the indexed
/// <c>_objects.value_string</c> column. Updates are NOT atomic across
/// processes — for strict cluster-wide budgets, swap in a SQL-backed
/// implementation with a row-level lock.
/// </summary>
public sealed class RedbCostBudgetStore : ICostBudgetStore
{
    private readonly IServiceScopeFactory _scopeFactory;
    private bool _schemeEnsured;
    private readonly SemaphoreSlim _ensureLock = new(1, 1);

    /// <summary>Creates the store. Scheme is synced lazily on first use.</summary>
    public RedbCostBudgetStore(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
    }

    /// <inheritdoc />
    public async ValueTask<AgentUsage> GetUsageAsync(string conversationId, CancellationToken ct = default)
    {
        await EnsureSchemeAsync().ConfigureAwait(false);
        var row = await LoadAsync(conversationId).ConfigureAwait(false);
        return row is null
            ? AgentUsage.Zero
            : new AgentUsage((int)row.Props.InputTokens, (int)row.Props.OutputTokens, row.Props.CostUsd);
    }

    /// <inheritdoc />
    public async ValueTask<AgentUsage> AddAsync(string conversationId, AgentUsage delta, CancellationToken ct = default)
    {
        await EnsureSchemeAsync().ConfigureAwait(false);

        using var scope = _scopeFactory.CreateScope();
        var redb = scope.ServiceProvider.GetRequiredService<IRedbService>();

        var row = await LoadAsync(conversationId, redb).ConfigureAwait(false);
        if (row is null)
        {
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
        }
        else
        {
            row.Props.InputTokens += delta.InputTokens;
            row.Props.OutputTokens += delta.OutputTokens;
            row.Props.CostUsd += delta.CostUsd;
            row.Props.UpdatedAtUtc = DateTimeOffset.UtcNow;
        }
        await redb.SaveAsync(row).ConfigureAwait(false);

        return new AgentUsage((int)row.Props.InputTokens, (int)row.Props.OutputTokens, row.Props.CostUsd);
    }

    /// <inheritdoc />
    public async ValueTask ResetAsync(string conversationId, CancellationToken ct = default)
    {
        await EnsureSchemeAsync().ConfigureAwait(false);

        using var scope = _scopeFactory.CreateScope();
        var redb = scope.ServiceProvider.GetRequiredService<IRedbService>();
        var row = await LoadAsync(conversationId, redb).ConfigureAwait(false);
        if (row is not null) await redb.DeleteAsync(row).ConfigureAwait(false);
    }

    private async Task<RedbObject<CostBudgetProps>?> LoadAsync(string conversationId, IRedbService? redb = null)
    {
        if (redb is null)
        {
            using var scope = _scopeFactory.CreateScope();
            redb = scope.ServiceProvider.GetRequiredService<IRedbService>();
            return await redb.Query<CostBudgetProps>()
                .WhereRedb(x => x.ValueString == conversationId)
                .FirstOrDefaultAsync()
                .ConfigureAwait(false);
        }

        return await redb.Query<CostBudgetProps>()
            .WhereRedb(x => x.ValueString == conversationId)
            .FirstOrDefaultAsync()
            .ConfigureAwait(false);
    }

    private async Task EnsureSchemeAsync()
    {
        if (_schemeEnsured) return;
        await _ensureLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_schemeEnsured) return;
            using var scope = _scopeFactory.CreateScope();
            var redb = scope.ServiceProvider.GetRequiredService<IRedbService>();
            await redb.SyncSchemeAsync<CostBudgetProps>().ConfigureAwait(false);
            _schemeEnsured = true;
        }
        finally { _ensureLock.Release(); }
    }
}

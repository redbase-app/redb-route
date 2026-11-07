using redb.Route.Llm.Engine.Storage;

namespace redb.Route.Llm.Engine.Governance;

/// <summary>
/// Budget enforcement that survives the run: usage is accumulated per conversation in an
/// <see cref="ICostBudgetStore"/>, and the per-run ceiling from <see cref="AgentBudget"/> is checked
/// against that running total. A conversation that already spent its budget therefore stops the next
/// run as well — that is the point of storing it, and it is a behaviour change worth knowing about:
/// a run that passes today can stop once the conversation's total crosses the ceiling.
/// <para>
/// Registered by <c>AddRedbLlmStorage()</c> in place of the shipped per-run defaults. The interface
/// has no exchange parameter, so store calls are made without one: the redb store opens a scope of its
/// own per call (concurrent runs never share a connection); per-exchange routing of budget rows is not
/// available to an enforcer.
/// </para>
/// <para>
/// Without a conversation id there is nowhere to accumulate, so the check degrades to the per-run one.
/// </para>
/// </summary>
public sealed class StoreBudgetEnforcer : IBudgetEnforcer
{
    private readonly ICostBudgetStore _store;
    private readonly InMemoryBudgetEnforcer _perRun = new();

    /// <summary>Creates the enforcer over <paramref name="store"/>.</summary>
    public StoreBudgetEnforcer(ICostBudgetStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    /// <inheritdoc />
    public async ValueTask<BudgetDecision> PreCheckAsync(
        string? conversationId, AgentBudget budget, AgentUsage usageSoFar, CancellationToken ct = default)
    {
        if (budget.IsUnbounded) return BudgetDecision.Allow;
        if (conversationId is null)
            return await _perRun.PreCheckAsync(null, budget, usageSoFar, ct).ConfigureAwait(false);

        var accumulated = await _store.GetUsageAsync(conversationId, exchange: null, ct).ConfigureAwait(false);
        return await _perRun.PreCheckAsync(conversationId, budget, accumulated, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<BudgetDecision> RecordAndCheckAsync(
        string? conversationId, AgentBudget budget, AgentUsage iterationUsage, AgentUsage totalUsage,
        CancellationToken ct = default)
    {
        if (budget.IsUnbounded) return BudgetDecision.Allow;
        if (conversationId is null)
            return await _perRun.RecordAndCheckAsync(null, budget, iterationUsage, totalUsage, ct).ConfigureAwait(false);

        var total = await _store.AddAsync(conversationId, iterationUsage, exchange: null, ct).ConfigureAwait(false);
        return await _perRun.RecordAndCheckAsync(conversationId, budget, iterationUsage, total, ct).ConfigureAwait(false);
    }
}
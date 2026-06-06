using System.Collections.Concurrent;
using redb.Route.Llm.Engine.Governance;

namespace redb.Route.Llm.Engine.Storage;

/// <summary>
/// Persists running cost / token usage per conversation so that
/// <see cref="IBudgetEnforcer"/> can enforce budgets across multiple agent
/// runs (not just within one). Implementations are expected to be safe for
/// concurrent updates — the engine writes after every iteration.
/// </summary>
public interface ICostBudgetStore
{
    /// <summary>Returns the running total for the conversation, or <see cref="AgentUsage.Zero"/> when unknown.</summary>
    ValueTask<AgentUsage> GetUsageAsync(string conversationId, CancellationToken ct = default);

    /// <summary>Atomically adds the iteration usage to the running total and returns the updated total.</summary>
    ValueTask<AgentUsage> AddAsync(string conversationId, AgentUsage delta, CancellationToken ct = default);

    /// <summary>Resets the running total — used after a billing period rollover or by tests.</summary>
    ValueTask ResetAsync(string conversationId, CancellationToken ct = default);
}

/// <summary>In-memory cost/token store. Replace with the redb-backed implementation in production.</summary>
public sealed class InMemoryCostBudgetStore : ICostBudgetStore
{
    private readonly ConcurrentDictionary<string, AgentUsage> _totals = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public ValueTask<AgentUsage> GetUsageAsync(string conversationId, CancellationToken ct = default) =>
        ValueTask.FromResult(_totals.TryGetValue(conversationId, out var u) ? u : AgentUsage.Zero);

    /// <inheritdoc />
    public ValueTask<AgentUsage> AddAsync(string conversationId, AgentUsage delta, CancellationToken ct = default)
    {
        var updated = _totals.AddOrUpdate(conversationId, delta, (_, current) => current.Add(delta));
        return ValueTask.FromResult(updated);
    }

    /// <inheritdoc />
    public ValueTask ResetAsync(string conversationId, CancellationToken ct = default)
    {
        _totals.TryRemove(conversationId, out _);
        return ValueTask.CompletedTask;
    }
}

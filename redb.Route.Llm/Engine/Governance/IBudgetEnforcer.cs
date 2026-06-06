namespace redb.Route.Llm.Engine.Governance;

/// <summary>
/// Enforces token and cost budgets on agent runs. Called before and after each
/// provider call so the engine can stop early or refuse an iteration when the
/// budget is exhausted.
/// </summary>
public interface IBudgetEnforcer
{
    /// <summary>
    /// Checks whether the agent run is allowed to continue at the start of an
    /// iteration. Implementations may inspect <paramref name="usageSoFar"/>
    /// against a configured budget and return <see cref="BudgetDecision.Stop"/>.
    /// </summary>
    /// <param name="conversationId">Conversation identifier (null when no persistence).</param>
    /// <param name="budget">Per-run budget (zero or negative components mean unlimited).</param>
    /// <param name="usageSoFar">Aggregated usage across iterations completed so far.</param>
    /// <param name="ct">Cancellation token.</param>
    ValueTask<BudgetDecision> PreCheckAsync(
        string? conversationId,
        AgentBudget budget,
        AgentUsage usageSoFar,
        CancellationToken ct = default);

    /// <summary>
    /// Records usage produced by the iteration and returns a decision whether
    /// the agent may continue. Implementations should accumulate usage on the
    /// underlying <see cref="Storage.ICostBudgetStore"/> when wired.
    /// </summary>
    ValueTask<BudgetDecision> RecordAndCheckAsync(
        string? conversationId,
        AgentBudget budget,
        AgentUsage iterationUsage,
        AgentUsage totalUsage,
        CancellationToken ct = default);
}

/// <summary>Per-run budget. Components ≤ 0 disable that dimension of enforcement.</summary>
public readonly record struct AgentBudget(int MaxInputTokens, int MaxOutputTokens, decimal MaxCostUsd)
{
    /// <summary>Unbounded budget — used when caller didn't supply one.</summary>
    public static readonly AgentBudget Unbounded = new(0, 0, 0m);

    /// <summary>True when every dimension is disabled (Unbounded).</summary>
    public bool IsUnbounded => MaxInputTokens <= 0 && MaxOutputTokens <= 0 && MaxCostUsd <= 0m;
}

/// <summary>Running usage measured against an <see cref="AgentBudget"/>.</summary>
public readonly record struct AgentUsage(int InputTokens, int OutputTokens, decimal CostUsd)
{
    /// <summary>Zero usage — used as the running total starting point.</summary>
    public static readonly AgentUsage Zero = new(0, 0, 0m);

    /// <summary>Adds two usages component-wise.</summary>
    public AgentUsage Add(AgentUsage other) =>
        new(InputTokens + other.InputTokens, OutputTokens + other.OutputTokens, CostUsd + other.CostUsd);
}

/// <summary>Outcome of a budget enforcement decision.</summary>
public readonly record struct BudgetDecision(bool Continue, string? Reason)
{
    /// <summary>Allows the iteration to proceed.</summary>
    public static readonly BudgetDecision Allow = new(true, null);

    /// <summary>Stops the agent run with a human-readable reason.</summary>
    public static BudgetDecision Stop(string reason) => new(false, reason);
}

/// <summary>Default no-op budget enforcer — always allows execution.</summary>
public sealed class NoopBudgetEnforcer : IBudgetEnforcer
{
    /// <inheritdoc />
    public ValueTask<BudgetDecision> PreCheckAsync(
        string? conversationId, AgentBudget budget, AgentUsage usageSoFar, CancellationToken ct = default)
        => ValueTask.FromResult(BudgetDecision.Allow);

    /// <inheritdoc />
    public ValueTask<BudgetDecision> RecordAndCheckAsync(
        string? conversationId, AgentBudget budget, AgentUsage iterationUsage, AgentUsage totalUsage, CancellationToken ct = default)
        => ValueTask.FromResult(BudgetDecision.Allow);
}

/// <summary>
/// In-process budget enforcer — checks <see cref="AgentBudget"/> components against
/// the running total. Does NOT persist between runs; use the redb-backed
/// <c>ICostBudgetStore</c> implementation for cross-run budgets.
/// </summary>
public sealed class InMemoryBudgetEnforcer : IBudgetEnforcer
{
    /// <inheritdoc />
    public ValueTask<BudgetDecision> PreCheckAsync(
        string? conversationId, AgentBudget budget, AgentUsage usageSoFar, CancellationToken ct = default)
    {
        if (budget.IsUnbounded) return ValueTask.FromResult(BudgetDecision.Allow);
        return ValueTask.FromResult(Check(budget, usageSoFar));
    }

    /// <inheritdoc />
    public ValueTask<BudgetDecision> RecordAndCheckAsync(
        string? conversationId, AgentBudget budget, AgentUsage iterationUsage, AgentUsage totalUsage, CancellationToken ct = default)
    {
        if (budget.IsUnbounded) return ValueTask.FromResult(BudgetDecision.Allow);
        return ValueTask.FromResult(Check(budget, totalUsage));
    }

    private static BudgetDecision Check(AgentBudget budget, AgentUsage usage)
    {
        if (budget.MaxInputTokens > 0 && usage.InputTokens >= budget.MaxInputTokens)
            return BudgetDecision.Stop($"input-token budget exceeded ({usage.InputTokens}/{budget.MaxInputTokens})");
        if (budget.MaxOutputTokens > 0 && usage.OutputTokens >= budget.MaxOutputTokens)
            return BudgetDecision.Stop($"output-token budget exceeded ({usage.OutputTokens}/{budget.MaxOutputTokens})");
        if (budget.MaxCostUsd > 0m && usage.CostUsd >= budget.MaxCostUsd)
            return BudgetDecision.Stop($"cost budget exceeded ({usage.CostUsd:F4}/{budget.MaxCostUsd:F4} USD)");
        return BudgetDecision.Allow;
    }
}

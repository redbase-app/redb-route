using redb.Route.Llm.Engine.Governance;

namespace redb.Route.Llm.Engine;

/// <summary>
/// Translates the nullable budget options of an endpoint / builder into an <see cref="AgentBudget"/>.
/// <para>
/// The nulls matter: <c>null</c> means "this dimension is not limited", while <see cref="AgentBudget"/>
/// spells that as <c>&lt;= 0</c>. Collapsing an unset option to zero would be harmless here (zero is
/// the same as unset for the enforcer) — but doing it in the URI would not, so the options are
/// appended only when set.
/// </para>
/// </summary>
internal static class AgentBudgetFactory
{
    /// <summary>Builds the per-run budget; all-null maps to <see cref="AgentBudget.Unbounded"/>.</summary>
    public static AgentBudget From(int? inputTokens, int? outputTokens, decimal? costUsd)
        => inputTokens is null && outputTokens is null && costUsd is null
            ? AgentBudget.Unbounded
            : new AgentBudget(inputTokens ?? 0, outputTokens ?? 0, costUsd ?? 0m);
}

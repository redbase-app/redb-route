using redb.Route.Llm.Abstractions.Tools;
using redb.Route.Llm.Engine.Governance;
using redb.Route.Llm.Providers;

namespace redb.Route.Llm.Engine.Eval;

/// <summary>
/// Declarative description of a single eval case. Combines the inputs the harness
/// feeds to <see cref="IAgentEngine.RunAsync"/> with the assertions it scores
/// against the produced <see cref="AgentResponse"/>. Cases are pure data so the
/// same set can be replayed offline (via <c>ReplayProvider</c>) and against a
/// live provider.
/// </summary>
public sealed class LlmEvalCase
{
    /// <summary>Stable scenario name — also persisted on <see cref="Storage.EvalRunRecord.Scenario"/>.</summary>
    public required string Scenario { get; init; }

    /// <summary>System prompt for the run. Null = no system prompt.</summary>
    public string? SystemPrompt { get; init; }

    /// <summary>User content blocks for the initial turn. Use <see cref="UserText"/> for the common text-only case.</summary>
    public IReadOnlyList<LlmContentBlock> UserContent { get; init; } = [];

    /// <summary>Tools available to the model on this case (empty = no tools).</summary>
    public IReadOnlyList<ILlmToolDescriptor> Tools { get; init; } = [];

    /// <summary>Maximum tool-loop iterations forwarded to <see cref="AgentRequest.MaxIterations"/>.</summary>
    public int MaxIterations { get; init; } = 4;

    /// <summary>Per-run budget forwarded to <see cref="AgentRequest.Budget"/>.</summary>
    public AgentBudget Budget { get; init; } = AgentBudget.Unbounded;

    /// <summary>Sampling temperature override.</summary>
    public double? Temperature { get; init; }

    /// <summary>Max-tokens override.</summary>
    public int? MaxTokens { get; init; }

    /// <summary>Substrings the final assistant text must contain (case-sensitive).</summary>
    public IReadOnlyList<string> ExpectedTextContains { get; init; } = [];

    /// <summary>Substrings the final assistant text must NOT contain (case-sensitive).</summary>
    public IReadOnlyList<string> ExpectedTextNotContains { get; init; } = [];

    /// <summary>Tool names that must be invoked at least once across the run (in any order).</summary>
    public IReadOnlyList<string> ExpectedToolCalls { get; init; } = [];

    /// <summary>When set, the run's final stop reason must equal this value.</summary>
    public LlmStopReason? ExpectedStopReason { get; init; }

    /// <summary>When set, the run's iteration count must be ≤ this value.</summary>
    public int? MaxObservedIterations { get; init; }

    /// <summary>When set, the run's total cost must be ≤ this value (USD).</summary>
    public decimal? MaxObservedCostUsd { get; init; }

    /// <summary>
    /// Optional custom scorer — receives the final <see cref="AgentResponse"/> and
    /// returns a <see cref="LlmEvalCaseScore"/>. Composes with the built-in
    /// assertions: a case fails when any built-in assertion fails OR the custom
    /// scorer returns <see cref="LlmEvalCaseScore.Pass"/> = false.
    /// </summary>
    public Func<AgentResponse, LlmEvalCaseScore>? CustomScorer { get; init; }

    /// <summary>Convenience: builds a single-text-block <see cref="UserContent"/>.</summary>
    public static IReadOnlyList<LlmContentBlock> UserText(string text) => [new LlmTextBlock(text)];
}

/// <summary>
/// Outcome of a custom scorer. Pass=false marks the case as failed regardless of
/// other assertions; <see cref="Score"/> and <see cref="Dimensions"/> flow into
/// the persisted <see cref="Storage.EvalRunRecord"/>.
/// </summary>
public readonly record struct LlmEvalCaseScore(
    bool Pass,
    double? Score = null,
    IReadOnlyDictionary<string, double>? Dimensions = null,
    string? Notes = null)
{
    /// <summary>Convenience: passing score with no extras.</summary>
    public static readonly LlmEvalCaseScore Passed = new(true);
}

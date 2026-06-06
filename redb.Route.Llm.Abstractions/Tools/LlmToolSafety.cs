namespace redb.Route.Llm.Abstractions.Tools;

/// <summary>
/// Governance metadata attached to every <see cref="LlmToolCapability"/>.
/// The engine uses these flags to decide approval requirements, idempotency
/// behaviour, caching, and budget-aware scheduling.
/// </summary>
public sealed class LlmToolSafety
{
    /// <summary>Side-effect classification. Defaults to <see cref="ToolSideEffect.ReadOnly"/>.</summary>
    public ToolSideEffect SideEffect { get; init; } = ToolSideEffect.ReadOnly;

    /// <summary>Caching policy hint. Defaults to <see cref="ToolCachingPolicy.None"/>.</summary>
    public ToolCachingPolicy Caching { get; init; } = ToolCachingPolicy.None;

    /// <summary>Cost class hint. Defaults to <see cref="ToolCostClass.Cheap"/>.</summary>
    public ToolCostClass Cost { get; init; } = ToolCostClass.Cheap;

    /// <summary>When true the tool always requires explicit user approval before execution.</summary>
    public bool RequiresApproval { get; init; }

    /// <summary>Claims required on the calling exchange's principal. Empty = no requirements.</summary>
    public IReadOnlyList<string> RequiredClaims { get; init; } = [];
}

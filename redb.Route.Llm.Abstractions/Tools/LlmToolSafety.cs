namespace redb.Route.Llm.Abstractions.Tools;

/// <summary>
/// Governance metadata attached to every <see cref="LlmToolCapability"/>. These are declarations:
/// some are enforced by the engine, some are reserved for later work. Every property states which,
/// so a reader never has to guess whether a field is a control point.
/// </summary>
public sealed class LlmToolSafety
{
    /// <summary>
    /// Side-effect classification. Defaults to <see cref="ToolSideEffect.ReadOnly"/>.
    /// <para><b>Enforced</b> — the engine reads it for two decisions. A tool that is not
    /// <see cref="ToolSideEffect.ReadOnly"/> is de-duplicated through the idempotency store (a replay
    /// of the same <c>tool_use</c> id returns the stored result instead of running again); a
    /// read-only tool is not reserved at all. Only a read-only tool may declare a caching policy.</para>
    /// <para><b>Default matters:</b> "declared nothing" and "declared ReadOnly" are indistinguishable,
    /// so a tool with no explicit class is treated as read-only — no replay protection. Set
    /// <see cref="ToolSideEffect.Mutating"/> on anything that changes state.</para>
    /// </summary>
    public ToolSideEffect SideEffect { get; init; } = ToolSideEffect.ReadOnly;

    /// <summary>
    /// Caching policy. Defaults to <see cref="ToolCachingPolicy.None"/>.
    /// <para><b>Enforced for read-only tools</b> — the engine answers a repeated identical call from a
    /// cache and reports the skip to observers. A mutating tool that declares a caching policy is
    /// rejected when its route is built.</para>
    /// </summary>
    public ToolCachingPolicy Caching { get; init; } = ToolCachingPolicy.None;

    /// <summary>
    /// Cost class hint. Defaults to <see cref="ToolCostClass.Cheap"/>.
    /// <para><b>Reserved</b> — budgets are computed from real provider usage, not from this hint.</para>
    /// </summary>
    public ToolCostClass Cost { get; init; } = ToolCostClass.Cheap;

    /// <summary>
    /// When true the tool requires an explicit approval decision before execution.
    /// <para><b>Enforced</b> — the engine awaits <c>IApprovalGate</c> before dispatching. Note that
    /// the shipped default gate approves automatically; a real control point requires replacing it.</para>
    /// </summary>
    public bool RequiresApproval { get; init; }

    /// <summary>
    /// Claims required on the calling exchange's principal. Empty = no requirements.
    /// <para><b>Enforced</b> — the engine asks the registered <c>IToolClaimsSource</c> and denies the
    /// call when the claims cannot be verified. The default source reads the caller's principal from
    /// the exchange (<c>ExchangePrincipal</c>, a property — not a header) and takes its <c>scope</c> /
    /// <c>scp</c> values; <c>AddRedbRouteLlm()</c> registers it, so claims work out of the box. A
    /// deployment with another vocabulary registers its own source. An unrelated identity header, an
    /// anonymous caller and a run with no inbound request all fail the check.</para>
    /// </summary>
    public IReadOnlyList<string> RequiredClaims { get; init; } = [];
}

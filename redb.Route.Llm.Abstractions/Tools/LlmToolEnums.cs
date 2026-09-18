namespace redb.Route.Llm.Abstractions.Tools;

/// <summary>
/// Side-effect classification declared by a tool author. Read by the engine for two decisions: a tool
/// whose <see cref="ToolSideEffect"/> is not <see cref="ToolSideEffect.ReadOnly"/> may not declare a
/// caching policy, because a cache hit suppresses the side effect the entry stands for; and an
/// <see cref="External"/> tool is not run inside an ambient transaction (a route's <c>.Transacted()</c>),
/// because that transaction can roll back while the external action cannot. Beyond that the
/// <c>Mutating</c> / <c>External</c> distinction stays declarative — it selects neither the approval gate
/// nor the idempotency policy.
/// </summary>
public enum ToolSideEffect
{
    /// <summary>Read-only: no state is mutated, so retries are harmless. The only class that may be cached.</summary>
    ReadOnly,

    /// <summary>Mutates internal state. Declared intent: a retry must carry an idempotency key.</summary>
    Mutating,

    /// <summary>Has external irreversible side-effects (mail, payment, deployment). Declared intent:
    /// requires approval — set <see cref="LlmToolSafety.RequiresApproval"/> explicitly, because
    /// approval is not derived from this value. Not run inside an ambient transaction: the model receives
    /// <see cref="ToolResultErrors.ExternalInTransaction"/> instead.</summary>
    External
}

/// <summary>
/// Result-caching policy for <see cref="LlmToolSafety"/>. Enforced for read-only tools: the engine
/// consults a cache and skips a call it can answer from it, reporting the skip to observers. A tool
/// that declares <c>Caching != None</c> together with a mutating side effect is rejected at route
/// build.
/// </summary>
public enum ToolCachingPolicy
{
    /// <summary>Do not cache.</summary>
    None,

    /// <summary>Memoise per (tool, resolved endpoint address, input) for the lifetime of the agent run,
    /// in process — the entry dies with the run and never reaches a store.</summary>
    Memoize,

    /// <summary>Memoise across runs in the configured cache store with a TTL (24 h). An output the
    /// redaction filter changed is not cached.</summary>
    Persist
}

/// <summary>Cost class hint for budget-aware scheduling. <b>Reserved</b> — budgets are computed from
/// real provider usage, not from this hint.</summary>
public enum ToolCostClass
{
    /// <summary>Sub-millisecond, no external IO.</summary>
    Cheap,

    /// <summary>Single-digit milliseconds, single network call.</summary>
    Moderate,

    /// <summary>Multi-second / external API / paid call.</summary>
    Expensive
}

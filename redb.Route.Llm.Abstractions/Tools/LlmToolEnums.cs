namespace redb.Route.Llm.Abstractions.Tools;

/// <summary>
/// Side-effect classification — used by the engine to decide approval requirements
/// and idempotency / retry semantics.
/// </summary>
public enum ToolSideEffect
{
    /// <summary>Read-only — safe to retry, safe to call without approval.</summary>
    ReadOnly,

    /// <summary>Mutates internal state — requires an idempotency key on retry.</summary>
    Mutating,

    /// <summary>Has external irreversible side-effects (mail, payment, deployment) — always requires approval.</summary>
    External
}

/// <summary>Result-caching policy hint for <see cref="LlmToolSafety"/>.</summary>
public enum ToolCachingPolicy
{
    /// <summary>Do not cache.</summary>
    None,

    /// <summary>Memoise per <c>(tool-name, input-hash)</c> for the lifetime of the agent run.</summary>
    Memoize,

    /// <summary>Persist across runs with the configured TTL on the cache store.</summary>
    Persist
}

/// <summary>Cost class hint for budget-aware scheduling.</summary>
public enum ToolCostClass
{
    /// <summary>Sub-millisecond, no external IO.</summary>
    Cheap,

    /// <summary>Single-digit milliseconds, single network call.</summary>
    Moderate,

    /// <summary>Multi-second / external API / paid call.</summary>
    Expensive
}

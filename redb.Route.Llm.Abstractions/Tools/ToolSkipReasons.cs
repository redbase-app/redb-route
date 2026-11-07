namespace redb.Route.Llm.Abstractions.Tools;

/// <summary>
/// Stable <c>SkipReason</c> values the engine reports to <c>IAgentObserver</c> when a tool call is
/// skipped instead of executed. This is the observer/audit channel; model-facing error codes live in
/// <see cref="ToolResultErrors"/>.
/// </summary>
public static class ToolSkipReasons
{
    /// <summary>
    /// Required claims could not be verified for the run's principal. The engine appends
    /// <c>:&lt;claim,claim&gt;</c> with the missing names — audit detail that must not reach the model.
    /// </summary>
    public const string ClaimsMissing = "claims_missing";

    /// <summary>
    /// The tool was answered from its cache instead of being dispatched, so the underlying side effect
    /// did not run. Only <see cref="ToolCachingPolicy.Memoize"/> and
    /// <see cref="ToolCachingPolicy.Persist"/> — that is, only read-only tools — can produce this.
    /// </summary>
    public const string CacheHit = "cache_hit";

    /// <summary>
    /// This <c>tool_use</c> id was already executed in the same conversation and its stored result was
    /// returned instead of running the tool again. Only tools with a declared side effect (not
    /// <see cref="ToolSideEffect.ReadOnly"/>) are de-duplicated this way.
    /// </summary>
    public const string IdempotencyHit = "idempotency_hit";

    /// <summary>
    /// The approval gate refused the call. The engine appends the gate's own reason text
    /// (<c>"denied: &lt;reason&gt;"</c>); the model sees only
    /// <see cref="ToolResultErrors.ApprovalDenied"/>.
    /// </summary>
    public const string ApprovalDeniedPrefix = "denied:";

    /// <summary>
    /// A <see cref="ToolSideEffect.External"/> tool was called while an ambient transaction was open (for
    /// example inside a route's <c>.Transacted()</c>). Its action cannot be rolled back with that
    /// transaction, so the engine did not run it; the model sees
    /// <see cref="ToolResultErrors.ExternalInTransaction"/>.
    /// </summary>
    public const string ExternalInTransaction = "external_in_transaction";
}

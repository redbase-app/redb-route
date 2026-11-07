using redb.Core.Attributes;

namespace redb.Route.Llm.Storage.Redb.Schemas;

/// <summary>
/// Cached deterministic tool output. The key is built by <c>Tools/ToolCacheKey.StoreKey</c> and shaped
/// <c>persist:{toolName}:{hash}</c> — the hash folds in the tool name, the resolved endpoint URI, the
/// governance-policy fingerprint, the caller fingerprint and the tool input JSON, so an entry written
/// while a tool was laxer can never answer a call made after the tool got stricter. Only
/// <c>Persist</c>-policy entries reach this store; <c>None</c> and <c>Memoize</c> never do (the latter
/// stays in the in-process <c>memo:</c> layer).
/// <para>
/// The key lives on the <c>value_string</c> column of <c>_objects</c> (partial index on
/// PostgreSQL/SQLite; no index on MSSQL), not in props — lookups are server-side via
/// <c>WhereRedb(o =&gt; o.ValueUnique == RedbUniqueKey.Normalize(key))</c>; the key also rides,
/// normalized, in <c>value_unique</c> (per-scheme unique race barrier).
/// The store honours <see cref="ExpiresAtUtc"/> lazily — entries past expiry are dropped on read.
/// </para>
/// </summary>
[RedbScheme("LLM Tool Cache")]
public class ToolCacheProps
{
    /// <summary>
    /// Optional tool name for scoped invalidation / metrics. Not written today — the tool name already
    /// rides in the clear inside the cache key (an <c>_objects</c> column), which is the only place a
    /// server-side query can reach it without touching props (<c>Tools/ToolCacheKey.cs</c>).
    /// </summary>
    public string? ToolName { get; set; }

    /// <summary>JSON-serialized tool output.</summary>
    public string OutputJson { get; set; } = "{}";

    /// <summary>When the entry was created.</summary>
    public DateTimeOffset CreatedAtUtc { get; set; }

    /// <summary>Optional expiry; null = no TTL.</summary>
    public DateTimeOffset? ExpiresAtUtc { get; set; }
}

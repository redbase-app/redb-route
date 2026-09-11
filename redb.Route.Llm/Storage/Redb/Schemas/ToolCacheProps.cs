using redb.Core.Attributes;

namespace redb.Route.Llm.Storage.Redb.Schemas;

/// <summary>
/// Cached deterministic tool output. The cache key (composite
/// <c>"llm-tool:{conv}:{toolUseId}"</c>) lives on the <c>value_string</c> column
/// of <c>_objects</c> (partial index on PostgreSQL/SQLite; no index on MSSQL),
/// not in props — lookups are server-side via
/// <c>WhereRedb(o =&gt; o.ValueString == key)</c>; new rows also carry the key,
/// normalized, in <c>value_unique</c> (per-scheme unique race barrier).
/// The store honours <see cref="ExpiresAtUtc"/> lazily — entries past expiry
/// are dropped on read.
/// </summary>
[RedbScheme("LLM Tool Cache")]
public class ToolCacheProps
{
    /// <summary>Optional tool name for scoped invalidation / metrics.</summary>
    public string? ToolName { get; set; }

    /// <summary>JSON-serialized tool output.</summary>
    public string OutputJson { get; set; } = "{}";

    /// <summary>When the entry was created.</summary>
    public DateTimeOffset CreatedAtUtc { get; set; }

    /// <summary>Optional expiry; null = no TTL.</summary>
    public DateTimeOffset? ExpiresAtUtc { get; set; }
}

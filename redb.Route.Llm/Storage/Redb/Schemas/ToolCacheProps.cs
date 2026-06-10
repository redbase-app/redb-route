using redb.Core.Attributes;

namespace redb.Route.Llm.Storage.Redb.Schemas;

/// <summary>
/// Cached deterministic tool output. The cache key (composite
/// <c>"llm-tool:{conv}:{toolUseId}"</c>) lives on the indexed
/// <c>value_string</c> column of <c>_objects</c>, not in props — lookups are
/// single-row server-side via <c>WhereRedb(o =&gt; o.ValueString == key)</c>.
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

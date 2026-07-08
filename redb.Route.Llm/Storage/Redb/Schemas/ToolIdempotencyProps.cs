using redb.Core.Attributes;

namespace redb.Route.Llm.Storage.Redb.Schemas;

/// <summary>
/// Persisted tool-call idempotency record. Distinct from
/// <see cref="ToolCacheProps"/>: this row exists for the lifetime of one
/// reservation cycle (reserve → complete → optional release) and answers
/// "did we already run tool_use_id X for conversation C?". The cache, by
/// contrast, is keyed on a content hash and lives on its own TTL.
/// <para>
/// Each row carries the composite key <c>"llm-tool:{conv}:{toolUseId}"</c> on
/// the indexed <c>_objects.value_string</c> column. Filtering by this scheme
/// id alone narrows the scan to idempotency rows — no mixing with cache rows
/// in <c>WhereRedb</c> queries.
/// </para>
/// </summary>
[RedbScheme("LLM Tool Idempotency")]
public class ToolIdempotencyProps
{
    /// <summary>Optional tool name for diagnostics / per-tool reports.</summary>
    public string? ToolName { get; set; }

    /// <summary>JSON-serialized tool output that was returned for the original call.</summary>
    public string OutputJson { get; set; } = "{}";

    /// <summary>When the original tool invocation was confirmed.</summary>
    public DateTimeOffset CreatedAtUtc { get; set; }
}

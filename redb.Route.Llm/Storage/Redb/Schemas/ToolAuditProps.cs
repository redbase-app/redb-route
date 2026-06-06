using redb.Core.Attributes;

namespace redb.Route.Llm.Storage.Redb.Schemas;

/// <summary>
/// One row per tool invocation, written by the audit observer. Designed so
/// <c>WhereRedb</c> queries on (<see cref="ConversationId"/>, <see cref="ToolName"/>,
/// <see cref="Outcome"/>, <see cref="InvokedAtUtc"/>) avoid the <c>_values</c>
/// table — these are the dimensions every audit dashboard slices by.
/// </summary>
[RedbScheme("LLM Tool Audit")]
public class ToolAuditProps
{
    /// <summary>Owning conversation.</summary>
    public string ConversationId { get; set; } = string.Empty;

    /// <summary>Message id this audit is attached to (the tool-use message).</summary>
    public string? MessageId { get; set; }

    /// <summary>Tool that was invoked.</summary>
    public string ToolName { get; set; } = string.Empty;

    /// <summary>Tool-use id from the model response.</summary>
    public string ToolUseId { get; set; } = string.Empty;

    /// <summary>When the invocation started.</summary>
    public DateTimeOffset InvokedAtUtc { get; set; }

    /// <summary>Wall-clock duration in milliseconds.</summary>
    public int DurationMs { get; set; }

    /// <summary>Outcome bucket: "success" / "error" / "skipped" / "denied".</summary>
    public string Outcome { get; set; } = string.Empty;

    /// <summary>Reason text when <see cref="Outcome"/> is "skipped" / "denied".</summary>
    public string? SkipReason { get; set; }

    /// <summary>JSON-serialized tool input as it was dispatched.</summary>
    public string InputJson { get; set; } = "{}";

    /// <summary>JSON-serialized tool output; null on failure.</summary>
    public string? OutputJson { get; set; }

    /// <summary>Exception message when <see cref="Outcome"/> is "error".</summary>
    public string? ErrorMessage { get; set; }
}

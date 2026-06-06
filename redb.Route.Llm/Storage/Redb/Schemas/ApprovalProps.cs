using redb.Core.Attributes;

namespace redb.Route.Llm.Storage.Redb.Schemas;

/// <summary>
/// Persisted decision from an <c>IApprovalGate</c>. The combination of
/// (<see cref="ConversationId"/>, <see cref="ToolName"/>, <see cref="Approved"/>,
/// <see cref="DecidedAtUtc"/>) at the top level allows reviewer queries
/// ("show every denial last week for tool X") without scanning value rows.
/// The approval id itself lives on the indexed <c>value_string</c> column of
/// <c>_objects</c>, not in props.
/// </summary>
[RedbScheme("LLM Approval")]
public class ApprovalProps
{
    /// <summary>Owning conversation (null for ad-hoc approvals).</summary>
    public string? ConversationId { get; set; }

    /// <summary>Tool that was gated.</summary>
    public string ToolName { get; set; } = string.Empty;

    /// <summary>Tool-use id from the model response.</summary>
    public string ToolUseId { get; set; } = string.Empty;

    /// <summary>True when the gate let the call proceed.</summary>
    public bool Approved { get; set; }

    /// <summary>Reason text supplied by the approver.</summary>
    public string? Reason { get; set; }

    /// <summary>Identifier of the approver (user / service) — null when auto-approved.</summary>
    public string? ApprovedBy { get; set; }

    /// <summary>When the decision was recorded.</summary>
    public DateTimeOffset DecidedAtUtc { get; set; }

    /// <summary>JSON-serialized tool input as it would be dispatched.</summary>
    public string InputJson { get; set; } = "{}";
}

using redb.Core.Attributes;

namespace redb.Route.Llm.Storage.Redb.Schemas;

/// <summary>
/// A single message node within a conversation tree. Persisted as a
/// <c>TreeRedbObject&lt;MessageProps&gt;</c> with the conversation root (or
/// another <see cref="MessageProps"/>) as parent — branching is native: any
/// regenerate / parallel-tool / what-if path attaches as another child of the
/// same parent.
/// <para>
/// Identifier lives in <c>value_string</c> (per-message GUID) and the
/// conversation FK lives in <c>value_long</c> (pointing at the root's
/// <c>_objects.id</c>) — both on the indexed <c>_objects</c> row, so transcript
/// queries never scan <c>_values</c>. Parent is tracked natively via the tree's
/// <c>parent_id</c>; no duplicate fields here.
/// </para>
/// Content blocks (text / tool-use / tool-result) are stored as a typed
/// nested array (<see cref="Content"/>) — REDB persists them natively, no
/// JSON-string marshalling.
/// </summary>
[RedbScheme("LLM Conversation Message")]
public class MessageProps
{
    /// <summary>Role: "user" / "assistant" / "system" / "tool".</summary>
    public string Role { get; set; } = string.Empty;

    /// <summary>Agent iteration that produced the message (0 = user prompt).</summary>
    public int Iteration { get; set; }

    /// <summary>When the message was appended.</summary>
    public DateTimeOffset CreatedAtUtc { get; set; }

    /// <summary>Provider id ("openai" / "anthropic" / ...); null for non-provider messages.</summary>
    public string? ProviderId { get; set; }

    /// <summary>Model id as resolved at call time.</summary>
    public string? ModelId { get; set; }

    /// <summary>Stop reason string for assistant messages; null otherwise.</summary>
    public string? StopReason { get; set; }

    /// <summary>Tool-use id when this message is a tool result; null otherwise.</summary>
    public string? ToolUseId { get; set; }

    /// <summary>Input tokens attributed to this turn (0 for non-assistant messages).</summary>
    public int InputTokens { get; set; }

    /// <summary>Output tokens attributed to this turn.</summary>
    public int OutputTokens { get; set; }

    /// <summary>
    /// Content blocks for this message, stored as a typed nested array. Each
    /// block carries a discriminator (<see cref="MessageContentBlock.Kind"/>)
    /// plus the fields used by the matching <c>LlmContentBlock</c> variant.
    /// Provider-emitted JSON (<see cref="MessageContentBlock.InputJson"/> /
    /// <see cref="MessageContentBlock.OutputJson"/>) stays a raw string —
    /// those payloads are intentionally schema-less.
    /// </summary>
    public MessageContentBlock[] Content { get; set; } = [];
}

/// <summary>
/// Typed nested representation of an <c>LlmContentBlock</c>. Discriminated
/// by <see cref="Kind"/>; only the fields used by the active variant are set.
/// </summary>
public class MessageContentBlock
{
    /// <summary>Block kind: "text" / "tool_use" / "tool_result".</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>Set when <see cref="Kind"/> == "text".</summary>
    public string? Text { get; set; }

    /// <summary>Set when <see cref="Kind"/> == "tool_use" or "tool_result".</summary>
    public string? ToolUseId { get; set; }

    /// <summary>Tool name; set when <see cref="Kind"/> == "tool_use".</summary>
    public string? ToolName { get; set; }

    /// <summary>Raw tool input JSON; set when <see cref="Kind"/> == "tool_use".</summary>
    public string? InputJson { get; set; }

    /// <summary>Raw tool output JSON; set when <see cref="Kind"/> == "tool_result".</summary>
    public string? OutputJson { get; set; }

    /// <summary>Error flag for tool results; set when <see cref="Kind"/> == "tool_result".</summary>
    public bool IsError { get; set; }
}

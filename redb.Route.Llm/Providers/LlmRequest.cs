using redb.Route.Llm.Abstractions.Tools;

namespace redb.Route.Llm.Providers;

/// <summary>Single message in the conversation transcript sent to the provider.</summary>
public sealed class LlmMessage
{
    /// <summary>"user", "assistant", "system", or "tool".</summary>
    public required string Role { get; init; }

    /// <summary>Ordered content blocks for this message.</summary>
    public IReadOnlyList<LlmContentBlock> Content { get; init; } = [];

    /// <summary>Convenience constructor for plain-text user messages.</summary>
    public static LlmMessage User(string text) => new()
    {
        Role = "user",
        Content = [new LlmTextBlock(text)]
    };

    /// <summary>Convenience constructor for plain-text assistant messages.</summary>
    public static LlmMessage Assistant(string text) => new()
    {
        Role = "assistant",
        Content = [new LlmTextBlock(text)]
    };
}

/// <summary>Base type for content blocks (text, tool-use, tool-result).</summary>
public abstract record LlmContentBlock;

/// <summary>Plain text block.</summary>
public sealed record LlmTextBlock(string Text) : LlmContentBlock;

/// <summary>
/// Tool-use block emitted by the assistant — engine must dispatch the tool and
/// reply with a matching <see cref="LlmToolResultBlock"/>.
/// </summary>
public sealed record LlmToolUseBlock(string ToolUseId, string Name, string InputJson) : LlmContentBlock;

/// <summary>Tool-result block sent back to the assistant on the next turn.</summary>
public sealed record LlmToolResultBlock(string ToolUseId, string OutputJson, bool IsError = false) : LlmContentBlock;

/// <summary>Token usage reported by the provider.</summary>
public sealed record LlmUsage(int InputTokens, int OutputTokens)
{
    /// <summary>Empty usage record.</summary>
    public static LlmUsage Empty { get; } = new(0, 0);
}

/// <summary>Reason the provider stopped generating.</summary>
public enum LlmStopReason
{
    /// <summary>End of assistant turn.</summary>
    EndTurn,
    /// <summary>Assistant requested one or more tool calls.</summary>
    ToolUse,
    /// <summary>Hit the max-tokens limit.</summary>
    MaxTokens,
    /// <summary>Hit a stop sequence configured on the request.</summary>
    StopSequence,
    /// <summary>Other / unknown reason — see provider-specific raw value.</summary>
    Other
}

/// <summary>Single completion request sent to a provider.</summary>
public sealed class LlmRequest
{
    /// <summary>Model identifier (overrides the factory default when set).</summary>
    public string? ModelId { get; init; }

    /// <summary>Optional system prompt.</summary>
    public string? SystemPrompt { get; init; }

    /// <summary>Conversation history including the current user turn.</summary>
    public IReadOnlyList<LlmMessage> Messages { get; init; } = [];

    /// <summary>Tool capabilities exposed to the model. Empty = no tools.</summary>
    public IReadOnlyList<LlmToolCapability> Tools { get; init; } = [];

    /// <summary>Sampling temperature.</summary>
    public double? Temperature { get; init; }

    /// <summary>Maximum output tokens.</summary>
    public int? MaxTokens { get; init; }

    /// <summary>Top-p sampling parameter.</summary>
    public double? TopP { get; init; }

    /// <summary>Optional stop sequences.</summary>
    public IReadOnlyList<string>? StopSequences { get; init; }
}

/// <summary>Single completion response from a provider.</summary>
public sealed class LlmResponse
{
    /// <summary>Content blocks produced by the assistant in this turn.</summary>
    public required IReadOnlyList<LlmContentBlock> Content { get; init; }

    /// <summary>Reason the provider stopped generating.</summary>
    public required LlmStopReason StopReason { get; init; }

    /// <summary>Token usage for this turn.</summary>
    public LlmUsage Usage { get; init; } = LlmUsage.Empty;

    /// <summary>Provider-native raw stop reason for diagnostics.</summary>
    public string? RawStopReason { get; init; }

    /// <summary>
    /// Opaque backend-configuration fingerprint surfaced by some providers
    /// (OpenAI's <c>system_fingerprint</c>; xAI / Together echo it; Anthropic
    /// does not). When two otherwise identical calls return different
    /// fingerprints, the model was re-released under the same id — the only
    /// signal an auditor has to detect silent provider drift.
    /// </summary>
    public string? ProviderSystemFingerprint { get; init; }
}

/// <summary>
/// Single chunk yielded by <see cref="ILlmProvider.StreamAsync"/>. Non-final chunks
/// have <see cref="StopReason"/> = null and partial <see cref="Content"/>.
/// </summary>
public sealed record LlmStreamChunk(
    IReadOnlyList<LlmContentBlock> Content,
    LlmStopReason? StopReason,
    LlmUsage? Usage);

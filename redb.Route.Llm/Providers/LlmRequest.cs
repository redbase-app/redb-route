using redb.Route.Llm.Abstractions.Tools;

namespace redb.Route.Llm.Providers;

/// <summary>Single message in the conversation transcript sent to the provider.</summary>
public sealed class LlmMessage
{
    /// <summary>"user", "assistant", "system", or "tool".</summary>
    public required string Role { get; init; }

    /// <summary>Ordered content blocks for this message.</summary>
    public IReadOnlyList<LlmContentBlock> Content { get; init; } = [];

    /// <summary>
    /// Ask the provider to place a prompt-cache breakpoint <b>after</b> this message: everything
    /// rendered up to and including it (tools, system, the messages before it, this one) becomes a
    /// cacheable prefix that later requests re-read instead of re-paying.
    ///
    /// <para>Meant for a byte-stable preamble that opens every conversation — a fixed opening
    /// exchange, a few-shot block — placed via <see cref="Engine.AgentRequest.Preamble"/>. The
    /// system-prompt breakpoint (<see cref="LlmRequest.CacheSystemPrompt"/>) stops at the system
    /// block; without a second one, a preamble sitting in <c>messages</c> is billed in full on
    /// every turn. Same rules as the system-prompt cache: the prefix must not change a byte, there
    /// is a model-dependent length floor, and providers without a cache concept ignore the flag.
    /// Anthropic allows four breakpoints per request; the system one, when asked for, is the
    /// first.</para>
    /// </summary>
    public bool CacheBreakpoint { get; init; }

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

/// <summary>Base type for content blocks (text, tool-use, tool-result, thinking).</summary>
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

/// <summary>
/// Token usage reported by the provider.
///
/// <para><b><paramref name="InputTokens"/> is the UNCACHED remainder, not the whole prompt.</b>
/// The prompt is the sum of all three: what was billed at full price, what was written to cache
/// this turn, and what was read back from it. A long agentic run that reports four thousand input
/// tokens has not shrunk — the rest came from cache.</para>
/// </summary>
/// <param name="InputTokens">Prompt tokens billed at full price.</param>
/// <param name="OutputTokens">Generated tokens.</param>
/// <param name="CacheCreationInputTokens">
/// Tokens written to the cache this turn, billed at a premium over normal input. Staying at zero
/// while caching is requested usually means the prefix is below the provider's length floor.
/// </param>
/// <param name="CacheReadInputTokens">
/// Tokens served from cache, billed at a fraction of normal input. **This is the number that says
/// caching works.** Zero across repeated calls with an unchanged prefix means something in that
/// prefix is not byte-stable.
/// </param>
public sealed record LlmUsage(
    int InputTokens,
    int OutputTokens,
    int CacheCreationInputTokens = 0,
    int CacheReadInputTokens = 0)
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

    /// <summary>
    /// Ask the provider to mark the system prompt as cacheable, so repeated turns re-read it
    /// instead of paying for it again.
    ///
    /// <para><b>Only worth setting when the system prompt is byte-stable across requests.</b>
    /// Caching is a prefix match: the provider renders tools, then system, then messages, and any
    /// byte change invalidates everything after it. A system prompt carrying a timestamp, a user
    /// name, or anything else that varies per request will simply never be read back — you pay
    /// the write premium for nothing.</para>
    ///
    /// <para><b>There is a length floor and it is model-dependent</b> (roughly 1–4k tokens on
    /// current Anthropic models). A shorter prefix silently does not cache: no error, and
    /// <c>cache_creation_input_tokens</c> stays at zero. That, and <c>cache_read_input_tokens</c>
    /// staying at zero across repeated calls, are the two things to look at when it seems not to
    /// work.</para>
    ///
    /// <para>Providers that have no cache concept ignore this flag.</para>
    /// </summary>
    public bool CacheSystemPrompt { get; init; }
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

    /// <summary>
    /// Provider-issued response identifier (OpenAI / xAI / Together: top-level
    /// <c>id</c>; Anthropic: top-level <c>id</c>). Used by auditors to
    /// cross-reference a persisted row with the provider's own usage / billing
    /// logs. Null when the provider does not surface one.
    /// </summary>
    public string? ProviderResponseId { get; init; }
}

/// <summary>
/// Single chunk yielded by <see cref="ILlmProvider.StreamAsync"/>. Non-final chunks have <see cref="StopReason"/> =
/// null and carry the pieces as they arrive: visible text as <see cref="LlmTextBlock"/>, the model's thinking as
/// <see cref="LlmThinkingBlock"/> without a signature. A piece is partial; the assembled blocks are in
/// <see cref="Response"/> of the last chunk, which also carries the completed tool calls in <see cref="Content"/>.
/// </summary>
public sealed record LlmStreamChunk(
    IReadOnlyList<LlmContentBlock> Content,
    LlmStopReason? StopReason,
    LlmUsage? Usage)
{
    /// <summary>
    /// The whole answer assembled from the stream, exactly what <see cref="ILlmProvider.CompleteAsync"/> returns for
    /// the same answer: blocks in the order they arrived, thinking with its signature, tool calls, usage, the stop
    /// reason and its raw value, the response id. Set on the last chunk only.
    /// </summary>
    public LlmResponse? Response { get; init; }
}

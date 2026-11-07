namespace redb.Route.Llm.Providers;

/// <summary>
/// Thinking / reasoning block returned by a provider that exposes its chain of thought —
/// Anthropic <c>thinking</c> and <c>redacted_thinking</c>, DeepSeek <c>reasoning_content</c>.
///
/// <para><b>The block is opaque unless the provider returns text.</b> It belongs to the assistant
/// turn that produced it and travels with that turn, unchanged and in the original order: on the
/// next request of the same run and, when a conversation store is configured, on a later run of the
/// same conversation, whichever provider that run asks. It is not an answer: it never reaches
/// <c>AgentResponse.Text</c> or <c>Out.Body</c>.</para>
///
/// <para>Each request path writes what its wire can express. Anthropic verifies
/// <see cref="Signature"/>, so that path sends a signed block or a redacted one exactly as received
/// and has no form for an unsigned block. <see cref="RedactedData"/> is the <c>redacted_thinking</c>
/// variant: an encrypted payload with no readable text. DeepSeek signs nothing; the OpenAI-compatible
/// path sends <see cref="Text"/> back as <c>reasoning_content</c> when the request declares tools.
/// The provider and model that produced a stored block are recorded on its message, not on the
/// block.</para>
/// </summary>
/// <param name="Text">Readable reasoning text; empty for a redacted block, and empty when the provider omits the text but still signs the block.</param>
/// <param name="Signature">Provider signature that authenticates <paramref name="Text"/>; null when the provider does not sign.</param>
/// <param name="RedactedData">Encrypted payload of a <c>redacted_thinking</c> block; null otherwise.</param>
public sealed record LlmThinkingBlock(
    string Text,
    string? Signature = null,
    string? RedactedData = null) : LlmContentBlock
{
    /// <summary>True for a <c>redacted_thinking</c> block: data without visible text.</summary>
    public bool IsRedacted => !string.IsNullOrEmpty(RedactedData);
}

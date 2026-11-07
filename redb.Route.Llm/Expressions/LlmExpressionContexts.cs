using redb.Route.Llm.Providers;

namespace redb.Route.Llm.Expressions;

/// <summary>
/// Snapshot of the current conversation visible to the
/// <c>${conversation.*}</c> expression family. Set by
/// <see cref="Engine.AgentEngine"/> on the parent exchange under
/// the well-known key <see cref="LlmExpressionKeys.Conversation"/>.
/// </summary>
public sealed class LlmConversationContext
{
    /// <summary>Conversation identifier (matches <c>AgentRequest.ConversationId</c>).</summary>
    public string? Id { get; init; }

    /// <summary>Number of messages in the transcript so far (includes the seeding user message).</summary>
    public int MessageCount { get; init; }

    /// <summary>Aggregate token usage across the current run.</summary>
    public LlmUsage Tokens { get; init; } = LlmUsage.Empty;

    /// <summary>Number of completed tool-loop iterations.</summary>
    public int Iterations { get; init; }

    /// <summary>Latest assistant message in the transcript (null before the first model response).</summary>
    public LlmMessage? LastMessage { get; init; }
}

/// <summary>
/// Snapshot of the tool currently being dispatched, visible to the
/// <c>${tool.*}</c> expression family. Set by
/// <see cref="Engine.AgentEngine"/> on the parent exchange under
/// the well-known key <see cref="LlmExpressionKeys.Tool"/>.
/// </summary>
public sealed class LlmToolContext
{
    /// <summary>Tool name as declared in the <c>tool_use</c> block.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Provider-issued tool use id (correlates the call to its result block).</summary>
    public string ToolUseId { get; init; } = string.Empty;

    /// <summary>Raw JSON input passed to the tool.</summary>
    public string InputJson { get; init; } = "{}";

    /// <summary>Raw JSON output produced by the tool. Null while the tool is in flight.</summary>
    public string? ResultJson { get; init; }

    /// <summary>Wall-clock execution duration. <see cref="System.TimeSpan.Zero"/> while in flight.</summary>
    public System.TimeSpan Duration { get; init; }
}

/// <summary>Well-known exchange property keys used by the LLM expression integration.</summary>
public static class LlmExpressionKeys
{
    /// <summary>Exchange property holding the current <see cref="LlmConversationContext"/>.</summary>
    public const string Conversation = "llm.conversation";

    /// <summary>Exchange property holding the current <see cref="LlmToolContext"/>.</summary>
    public const string Tool = "llm.tool";
}

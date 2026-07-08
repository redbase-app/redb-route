using redb.Route.Abstractions;
using redb.Route.Llm.Abstractions.Tools;
using redb.Route.Llm.Engine.Governance;
using redb.Route.Llm.Providers;

namespace redb.Route.Llm.Engine;

/// <summary>
/// Single agent execution request. Carries the user message, optional system prompt,
/// conversation context and the parent exchange for TX/scope/principal.
/// </summary>
public sealed class AgentRequest
{
    /// <summary>Connection factory used to build the provider.</summary>
    public required LlmConnectionFactory Factory { get; init; }

    /// <summary>Owning exchange — drives DI scope, headers, principal and observability.</summary>
    public required IExchange Exchange { get; init; }

    /// <summary>Initial user message (may include text and tool-result blocks).</summary>
    public required IReadOnlyList<LlmContentBlock> UserContent { get; init; }

    /// <summary>System prompt for this run.</summary>
    public string? SystemPrompt { get; init; }

    /// <summary>Tools available to the model on this run.</summary>
    public IReadOnlyList<ILlmToolDescriptor> Tools { get; init; } = [];

    /// <summary>Conversation identifier (when conversation tracking is on).</summary>
    public string? ConversationId { get; init; }

    /// <summary>
    /// Message id of the conversation node this run should attach under.
    /// Null = attach under the conversation root (continue at head when resuming;
    /// new branch when the conversation already has children).
    /// </summary>
    public string? ConversationParentMessageId { get; init; }

    /// <summary>Per-run budget (defaults to unbounded).</summary>
    public AgentBudget Budget { get; init; } = AgentBudget.Unbounded;

    /// <summary>Maximum tool-loop iterations.</summary>
    public int MaxIterations { get; init; } = 8;

    /// <summary>Per-call sampling overrides.</summary>
    public double? Temperature { get; init; }

    /// <summary>Per-call max-tokens override.</summary>
    public int? MaxTokens { get; init; }

    /// <summary>
    /// Optional name of the prompt template that produced <see cref="SystemPrompt"/>.
    /// When set together with <see cref="PromptTemplateVersion"/>, the engine
    /// stamps every persisted assistant message with the (name, version) pair so
    /// auditors can reconstruct "which exact prompt drove this answer". Stays
    /// null when the caller does not use a managed template.
    /// </summary>
    public string? PromptTemplateName { get; init; }

    /// <summary>Version of the prompt template named by <see cref="PromptTemplateName"/>.</summary>
    public string? PromptTemplateVersion { get; init; }

    /// <summary>
    /// Stable identifier of the principal that initiated this run (end-user,
    /// service account, on-behalf-of subject — host decides). Persisted on
    /// every row of the run so audit queries always have a consistent subject.
    /// Null when no principal is wired.
    /// </summary>
    public string? UserId { get; init; }

    /// <summary>
    /// Free-form audit tags captured pre-call (operator-controlled key/value
    /// pairs — e.g. <c>ab-bucket=A</c>, <c>tier=premium</c>). The same snapshot
    /// is stamped on every row of the run; null / empty when no tags are wired.
    /// </summary>
    public IReadOnlyDictionary<string, string>? AuditTags { get; init; }
}

/// <summary>Final agent response.</summary>
public sealed class AgentResponse
{
    /// <summary>Last assistant content (text + any unanswered tool calls).</summary>
    public required IReadOnlyList<LlmContentBlock> Content { get; init; }

    /// <summary>Aggregate token usage across all iterations.</summary>
    public LlmUsage Usage { get; init; } = LlmUsage.Empty;

    /// <summary>Number of tool-loop iterations consumed.</summary>
    public int Iterations { get; init; }

    /// <summary>Final stop reason of the last provider call.</summary>
    public LlmStopReason StopReason { get; init; } = LlmStopReason.EndTurn;

    /// <summary>Convenience accessor — first text block in <see cref="Content"/>, or empty.</summary>
    public string Text
    {
        get
        {
            foreach (var b in Content)
                if (b is LlmTextBlock t) return t.Text;
            return string.Empty;
        }
    }
}

using redb.Core.Attributes;

namespace redb.Route.Llm.Storage.Redb.Schemas;

/// <summary>
/// Root record of a single LLM conversation. Top-level scalar fields are
/// hoisted so that <c>WhereRedb</c> queries (by tenant, status, last activity)
/// hit the <c>_objects</c> table directly without scanning <c>_values</c>.
/// Message nodes are stored as child <see cref="MessageProps"/> via
/// <c>TreeRedbObject</c>; this record holds only conversation-level counters
/// and metadata.
/// </summary>
[RedbScheme("LLM Conversation")]
public class ConversationProps
{
    /// <summary>Tenant or organization owning the conversation.</summary>
    public string TenantId { get; set; } = string.Empty;

    /// <summary>Human-readable title; null when not yet set by the agent.</summary>
    public string? Title { get; set; }

    /// <summary>Lifecycle state: "active" / "closed" / "archived".</summary>
    public string Status { get; set; } = "active";

    /// <summary>When the conversation was created.</summary>
    public DateTimeOffset StartedAtUtc { get; set; }

    /// <summary>When the most recent message was appended.</summary>
    public DateTimeOffset LastActivityAtUtc { get; set; }

    /// <summary>Cumulative input tokens across every iteration of every run.</summary>
    public long TotalInputTokens { get; set; }

    /// <summary>Cumulative output tokens across every iteration of every run.</summary>
    public long TotalOutputTokens { get; set; }

    /// <summary>Cumulative cost in USD across the conversation.</summary>
    public decimal TotalCostUsd { get; set; }

    /// <summary>Number of agent runs (resumes) that have touched the conversation.</summary>
    public int RunCount { get; set; }
}

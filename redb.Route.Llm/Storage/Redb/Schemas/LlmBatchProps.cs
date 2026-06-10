using redb.Core.Attributes;

namespace redb.Route.Llm.Storage.Redb.Schemas;

/// <summary>
/// A submitted async-batch job tracked by <see cref="redb.Route.Llm.Engine.Storage.IBatchStore"/>.
/// Carries the provider-issued batch identifier on
/// <c>_objects.value_string</c> for direct lookup, and an optional conversation
/// id on <c>_objects.value_long</c> when the caller pre-bound the batch to a
/// conversation. Status transitions ("submitted", "running", "completed",
/// "failed") are written by <see cref="redb.Route.Llm.Engine.Storage.IBatchStore.MarkCompletedAsync"/>
/// and friends — the framework does not poll on its own; status is updated
/// either when the corresponding webhook arrives or when the host explicitly
/// calls <c>UpdateStatusAsync</c>.
/// </summary>
[RedbScheme("LLM Batch Job")]
public class LlmBatchProps
{
    /// <summary>Provider identifier — "anthropic", "openai", custom values for vLLM/Ollama batch endpoints.</summary>
    public string ProviderId { get; set; } = string.Empty;

    /// <summary>Model id at submit-time. Useful when a model version rolls forward between submit and callback.</summary>
    public string ModelId { get; set; } = string.Empty;

    /// <summary>Conversation correlation id — empty when the batch is conversation-less.</summary>
    public string ConversationId { get; set; } = string.Empty;

    /// <summary>Lifecycle: "submitted" → "running" → "completed" / "failed" / "cancelled".</summary>
    public string Status { get; set; } = "submitted";

    /// <summary>When the host submitted the batch.</summary>
    public DateTimeOffset SubmittedAtUtc { get; set; }

    /// <summary>When the callback fired (or the host marked completion).</summary>
    public DateTimeOffset? CompletedAtUtc { get; set; }

    /// <summary>Optional URL where the host can fetch results — null when the provider returns them inline in the callback.</summary>
    public string? ResultUrl { get; set; }

    /// <summary>Free-form metadata supplied by the caller at submit time (caller-controlled JSON).</summary>
    public string? MetadataJson { get; set; }

    /// <summary>Last error message recorded against the batch — null while healthy.</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// When <c>true</c> the callback processor appends the assistant turn from
    /// the callback body to the conversation store on completion. See
    /// <see cref="redb.Route.Llm.Engine.Storage.BatchJobRecord.AppendToConversation"/>.
    /// </summary>
    public bool AppendToConversation { get; set; }
}

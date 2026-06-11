using System.Collections.Concurrent;
using redb.Route.Abstractions;
using redb.Route.Llm.Providers;

namespace redb.Route.Llm.Engine.Storage;

/// <summary>
/// Persists conversation transcripts so that an agent run can be resumed,
/// branched, or replayed offline. The contract is intentionally tree-shaped:
/// every message has an optional parent identifier, which lets callers branch
/// from any node — required to model regenerate / parallel tool-calls / what-if
/// flows. The flat "append-only history" pattern falls out naturally when the
/// caller always passes the previously-appended message id as <c>parentId</c>.
/// <para>
/// The optional <c>exchange</c> parameter on every method carries the current
/// route exchange when the call originates from inside a route pipeline.
/// REDB-backed implementations resolve a per-exchange <c>IRedbService</c>
/// (and its named siblings via <see cref="LlmKeys.RedbName"/>) through
/// <c>IRouteContext.GetRedbService(name, exchange)</c>, which already manages
/// per-exchange scopes and disposal. In-memory implementations ignore it.
/// </para>
/// </summary>
public interface IConversationStore
{
    /// <summary>
    /// Appends <paramref name="message"/> as a child of <paramref name="parentId"/>
    /// (or as the root when <paramref name="parentId"/> is null) and returns the
    /// new message identifier.
    /// </summary>
    Task<string> AppendAsync(
        string conversationId,
        string? parentId,
        LlmMessage message,
        ConversationMessageMeta meta,
        IExchange? exchange = null,
        CancellationToken ct = default);

    /// <summary>
    /// Loads the linear path from the conversation root to <paramref name="leafId"/>.
    /// Passing null returns the path to the most-recently-appended leaf.
    /// </summary>
    Task<IReadOnlyList<ConversationMessage>> LoadPathAsync(
        string conversationId,
        string? leafId = null,
        IExchange? exchange = null,
        CancellationToken ct = default);

    /// <summary>
    /// Loads the full tree for the conversation (every branch). Used for
    /// observability dashboards and eval replays — NOT for hot-path requests.
    /// </summary>
    Task<IReadOnlyList<ConversationMessage>> LoadTreeAsync(
        string conversationId,
        IExchange? exchange = null,
        CancellationToken ct = default);
}

/// <summary>Queryable metadata that lives on the message node — hoisted from
/// the message body so that index-friendly filters (date range, role,
/// iteration) can run without scanning the full payload.</summary>
public sealed class ConversationMessageMeta
{
    /// <summary>Wall-clock timestamp the message was appended.</summary>
    public DateTime CreatedAtUtc { get; init; } = DateTime.UtcNow;

    /// <summary>Agent iteration index (1-based) that produced the message; 0 for the user prompt.</summary>
    public int Iteration { get; init; }

    /// <summary>Provider id ("openai", "anthropic", ...) — null for non-provider messages.</summary>
    public string? ProviderId { get; init; }

    /// <summary>Model id as resolved at the time of the call.</summary>
    public string? ModelId { get; init; }

    /// <summary>Stop reason for assistant messages.</summary>
    public LlmStopReason? StopReason { get; init; }

    /// <summary>Token usage attributed to this turn.</summary>
    public LlmUsage Usage { get; init; } = LlmUsage.Empty;

    /// <summary>Tool-use id when the message is a tool result.</summary>
    public string? ToolUseId { get; init; }

    /// <summary>
    /// Effective sampling temperature passed to the provider for the call
    /// that produced this assistant message; null on user / tool-result rows.
    /// </summary>
    public double? Temperature { get; init; }

    /// <summary>Effective max-output-tokens cap; null on non-assistant rows.</summary>
    public int? MaxTokens { get; init; }

    /// <summary>Effective top-p value; null on non-assistant rows.</summary>
    public double? TopP { get; init; }

    /// <summary>Prompt-template name resolved at call time (FK into <c>PromptTemplateProps</c>).</summary>
    public string? PromptTemplateName { get; init; }

    /// <summary>Prompt-template version paired with <see cref="PromptTemplateName"/>.</summary>
    public string? PromptTemplateVersion { get; init; }

    /// <summary>
    /// Stable hash of the tool-capabilities set exposed to the model on this
    /// call. Allows auditors to detect tool-surface drift across runs.
    /// </summary>
    public string? ToolSetHash { get; init; }

    /// <summary>
    /// Opaque provider-side fingerprint identifying the backend configuration
    /// that served the call (OpenAI's <c>system_fingerprint</c>). Null when the
    /// provider does not surface one.
    /// </summary>
    public string? ProviderSystemFingerprint { get; init; }
}

/// <summary>A persisted conversation message with its tree placement and metadata.</summary>
public sealed class ConversationMessage
{
    /// <summary>Store-assigned message id.</summary>
    public required string Id { get; init; }

    /// <summary>Parent message id; null for the root.</summary>
    public string? ParentId { get; init; }

    /// <summary>Conversation this message belongs to.</summary>
    public required string ConversationId { get; init; }

    /// <summary>Persisted message payload.</summary>
    public required LlmMessage Message { get; init; }

    /// <summary>Metadata captured at append time.</summary>
    public required ConversationMessageMeta Meta { get; init; }
}

/// <summary>
/// In-memory conversation store — suitable for unit tests and demos.
/// Replace with the redb-backed implementation in <c>redb.Route.Llm.Storage.Redb</c>.
/// </summary>
public sealed class InMemoryConversationStore : IConversationStore
{
    private readonly ConcurrentDictionary<string, List<ConversationMessage>> _byConversation = new();

    /// <inheritdoc />
    public Task<string> AppendAsync(string conversationId, string? parentId, LlmMessage message, ConversationMessageMeta meta, IExchange? exchange = null, CancellationToken ct = default)
    {
        var id = Guid.NewGuid().ToString("N");
        var node = new ConversationMessage
        {
            Id = id,
            ParentId = parentId,
            ConversationId = conversationId,
            Message = message,
            Meta = meta
        };

        var bucket = _byConversation.GetOrAdd(conversationId, _ => new List<ConversationMessage>());
        lock (bucket) bucket.Add(node);
        return Task.FromResult(id);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<ConversationMessage>> LoadPathAsync(string conversationId, string? leafId = null, IExchange? exchange = null, CancellationToken ct = default)
    {
        if (!_byConversation.TryGetValue(conversationId, out var bucket))
            return Task.FromResult<IReadOnlyList<ConversationMessage>>([]);

        ConversationMessage[] snapshot;
        lock (bucket) snapshot = bucket.ToArray();

        var leaf = leafId is null
            ? snapshot.LastOrDefault()
            : snapshot.FirstOrDefault(m => m.Id == leafId);

        if (leaf is null) return Task.FromResult<IReadOnlyList<ConversationMessage>>([]);

        var byId = snapshot.ToDictionary(m => m.Id);
        var path = new List<ConversationMessage>();
        var cursor = leaf;
        while (cursor is not null)
        {
            path.Add(cursor);
            cursor = cursor.ParentId is { } pid && byId.TryGetValue(pid, out var parent) ? parent : null;
        }
        path.Reverse();
        return Task.FromResult<IReadOnlyList<ConversationMessage>>(path);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<ConversationMessage>> LoadTreeAsync(string conversationId, IExchange? exchange = null, CancellationToken ct = default)
    {
        if (!_byConversation.TryGetValue(conversationId, out var bucket))
            return Task.FromResult<IReadOnlyList<ConversationMessage>>([]);
        lock (bucket) return Task.FromResult<IReadOnlyList<ConversationMessage>>(bucket.ToArray());
    }
}

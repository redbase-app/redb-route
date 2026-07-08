using System.Collections.Concurrent;
using redb.Route.Abstractions;

namespace redb.Route.Llm.Engine.Storage;

/// <summary>
/// Tracks async-batch jobs submitted to LLM providers (Anthropic Message
/// Batches, OpenAI Batch API, vLLM batch endpoints). The framework does not
/// prescribe a submit protocol — providers stay free to implement
/// <c>SubmitAsync</c> however they like — but every submitted batch should be
/// registered here so that incoming webhooks can be correlated, deduplicated
/// and routed back to the originating conversation.
/// <para>
/// Idempotency of the callback itself is the caller's responsibility (use
/// <see cref="IToolIdempotencyStore"/> with the batch id as key); this store
/// only persists the submit-time metadata.
/// </para>
/// <para>
/// The optional <c>exchange</c> parameter on every method carries the route
/// pipeline's current exchange; REDB-backed implementations resolve a
/// per-exchange <see cref="redb.Core.IRedbService"/> through
/// <c>IRouteContext.GetRedbService(name, exchange)</c> using the named-redb
/// hint stored in <see cref="LlmKeys.RedbName"/>. In-memory implementations
/// ignore it.
/// </para>
/// </summary>
public interface IBatchStore
{
    /// <summary>Records a batch submission. Replaces an existing row with the same id.</summary>
    Task RegisterAsync(BatchJobRecord record, IExchange? exchange = null, CancellationToken ct = default);

    /// <summary>
    /// Records many batch submissions at once. Bulk-friendly stores commit the
    /// whole set in a single transaction; the default falls back to a loop.
    /// </summary>
    async Task RegisterManyAsync(IEnumerable<BatchJobRecord> records, IExchange? exchange = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(records);
        foreach (var record in records)
        {
            ct.ThrowIfCancellationRequested();
            await RegisterAsync(record, exchange, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Loads a previously-registered batch by id; null if unknown.</summary>
    Task<BatchJobRecord?> GetAsync(string batchId, IExchange? exchange = null, CancellationToken ct = default);

    /// <summary>
    /// Updates lifecycle status and optional fields. Unknown ids are ignored
    /// silently — callers should treat a missing row as "not ours" and not as
    /// an error condition.
    /// </summary>
    Task UpdateStatusAsync(
        string batchId,
        string status,
        DateTimeOffset? completedAtUtc = null,
        string? errorMessage = null,
        IExchange? exchange = null,
        CancellationToken ct = default);

    /// <summary>Marks a batch as completed; convenience wrapper around <see cref="UpdateStatusAsync"/>.</summary>
    Task MarkCompletedAsync(string batchId, IExchange? exchange = null, CancellationToken ct = default) =>
        UpdateStatusAsync(batchId, "completed", DateTimeOffset.UtcNow, null, exchange, ct);

    /// <summary>Marks a batch as failed; convenience wrapper around <see cref="UpdateStatusAsync"/>.</summary>
    Task MarkFailedAsync(string batchId, string errorMessage, IExchange? exchange = null, CancellationToken ct = default) =>
        UpdateStatusAsync(batchId, "failed", DateTimeOffset.UtcNow, errorMessage, exchange, ct);
}

/// <summary>A persisted batch-submission record.</summary>
public sealed class BatchJobRecord
{
    /// <summary>Provider-issued batch identifier — the business key.</summary>
    public required string BatchId { get; init; }

    /// <summary>"anthropic", "openai", or any custom provider id.</summary>
    public required string ProviderId { get; init; }

    /// <summary>Model id at submit-time.</summary>
    public string ModelId { get; init; } = string.Empty;

    /// <summary>Optional conversation correlation; empty when the batch is conversation-less.</summary>
    public string ConversationId { get; init; } = string.Empty;

    /// <summary>Lifecycle: "submitted" / "running" / "completed" / "failed" / "cancelled".</summary>
    public string Status { get; init; } = "submitted";

    /// <summary>When the host submitted the batch.</summary>
    public DateTime SubmittedAtUtc { get; init; } = DateTime.UtcNow;

    /// <summary>When the callback fired (or the host marked completion); null while pending.</summary>
    public DateTime? CompletedAtUtc { get; init; }

    /// <summary>Optional URL where the host can fetch results; null when results arrive inline.</summary>
    public string? ResultUrl { get; init; }

    /// <summary>Free-form metadata captured at submit time (caller-controlled JSON).</summary>
    public string? MetadataJson { get; init; }

    /// <summary>Last error message recorded against the batch; null while healthy.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Opt-in: when <c>true</c> and the batch resolves to <c>"completed"</c>,
    /// <see cref="redb.Route.Llm.LlmCallbackProcessor"/> appends the assistant
    /// turn extracted from the callback body to <see cref="IConversationStore"/>
    /// under <see cref="ConversationId"/>. Has no effect when the conversation
    /// id is empty or the conversation store is not registered. Default <c>false</c>.
    /// </summary>
    public bool AppendToConversation { get; init; }
}

/// <summary>In-memory <see cref="IBatchStore"/> — entries live only for the process lifetime.</summary>
public sealed class InMemoryBatchStore : IBatchStore
{
    private readonly ConcurrentDictionary<string, BatchJobRecord> _rows = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public Task RegisterAsync(BatchJobRecord record, IExchange? exchange = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentException.ThrowIfNullOrWhiteSpace(record.BatchId);
        _rows[record.BatchId] = record;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<BatchJobRecord?> GetAsync(string batchId, IExchange? exchange = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(batchId);
        return Task.FromResult(_rows.TryGetValue(batchId, out var hit) ? hit : null);
    }

    /// <inheritdoc />
    public Task UpdateStatusAsync(
        string batchId,
        string status,
        DateTimeOffset? completedAtUtc = null,
        string? errorMessage = null,
        IExchange? exchange = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(batchId);
        ArgumentException.ThrowIfNullOrWhiteSpace(status);

        if (!_rows.TryGetValue(batchId, out var existing)) return Task.CompletedTask;

        var updated = new BatchJobRecord
        {
            BatchId = existing.BatchId,
            ProviderId = existing.ProviderId,
            ModelId = existing.ModelId,
            ConversationId = existing.ConversationId,
            Status = status,
            SubmittedAtUtc = existing.SubmittedAtUtc,
            CompletedAtUtc = completedAtUtc?.UtcDateTime ?? existing.CompletedAtUtc,
            ResultUrl = existing.ResultUrl,
            MetadataJson = existing.MetadataJson,
            ErrorMessage = errorMessage ?? existing.ErrorMessage,
            AppendToConversation = existing.AppendToConversation
        };
        _rows[batchId] = updated;
        return Task.CompletedTask;
    }
}

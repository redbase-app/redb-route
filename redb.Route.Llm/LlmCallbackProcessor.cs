using System.Text.Json;
using redb.Route.Abstractions;
using redb.Route.Llm.Engine.Storage;
using redb.Route.Llm.Providers;

namespace redb.Route.Llm;

/// <summary>
/// Processes inbound webhook callbacks from async-batch LLM APIs (Anthropic
/// Message Batches, OpenAI Batch, vLLM batch endpoints, ...).
/// <para>
/// Wire it into a vanilla HTTP route — no <c>llm-callback://</c> scheme:
/// <code>
/// r.From("http:0.0.0.0:5099/api/llm/callback?inOut=true")
///     .Process(new LlmCallbackProcessor())
///     .Choice()
///         .When(e =&gt; (bool)e.In.Headers[LlmHeaders.BatchDuplicate]!).Stop()
///     .End()
///     .To("direct:llm-batch-handler");
/// </code>
/// </para>
/// <para>
/// Behaviour:
/// <list type="number">
/// <item>Resolve the batch id (header → query → JSON body in this order).</item>
/// <item>Deduplicate via <see cref="IToolIdempotencyStore"/>, keyed on
/// <c>"batch:&lt;batchId&gt;"</c>. A duplicate sets
/// <see cref="LlmHeaders.BatchDuplicate"/>=<c>true</c> and stops further work.</item>
/// <item>If an <see cref="IBatchStore"/> is registered, load the original
/// submission to populate <see cref="LlmHeaders.ConversationId"/>,
/// <see cref="LlmHeaders.ProviderId"/>, <see cref="LlmHeaders.ModelId"/>.</item>
/// <item>Mark the batch completed and confirm idempotency.</item>
/// </list>
/// </para>
/// <para>
/// The processor never fetches results from the provider on its own — that
/// step belongs to a follow-up route or the host application. It only
/// guarantees a single, conversation-aware delivery of every callback.
/// </para>
/// </summary>
public sealed class LlmCallbackProcessor : IProcessor
{
    private readonly LlmCallbackOptions _options;
    private readonly IToolIdempotencyStore _idempotency;
    private readonly IBatchStore? _batchStore;
    private readonly IConversationStore? _conversationStore;

    /// <summary>Creates a callback processor.</summary>
    /// <param name="idempotency">Required — used to deduplicate batch ids.</param>
    /// <param name="batchStore">Optional — when present, the processor enriches headers with submit-time metadata.</param>
    /// <param name="conversationStore">
    /// Optional — when present together with <paramref name="batchStore"/>, completed callbacks whose record
    /// has <see cref="BatchJobRecord.AppendToConversation"/> set are appended to the conversation as
    /// an assistant turn (extracted from the callback body via <see cref="LlmCallbackOptions.AssistantContentJsonFields"/>).
    /// </param>
    /// <param name="options">Optional — overrides for header / query / JSON-body field names.</param>
    public LlmCallbackProcessor(
        IToolIdempotencyStore idempotency,
        IBatchStore? batchStore = null,
        IConversationStore? conversationStore = null,
        LlmCallbackOptions? options = null)
    {
        _idempotency = idempotency ?? throw new ArgumentNullException(nameof(idempotency));
        _batchStore = batchStore;
        _conversationStore = conversationStore;
        _options = options ?? new LlmCallbackOptions();
    }

    /// <inheritdoc />
    public async Task Process(IExchange exchange, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(exchange);

        var batchId = ResolveBatchId(exchange)
            ?? throw new InvalidOperationException(
                $"LlmCallbackProcessor: batch id not found. Looked at header '{_options.BatchIdHeader}', " +
                $"query parameter '{_options.BatchIdQueryParam}', JSON fields {string.Join(", ", _options.BatchIdJsonFields)}.");

        var status = ResolveStatus(exchange);

        // Pre-create Out so the rest of the route sees the enrichments.
        exchange.Out ??= exchange.In.Clone();
        exchange.Out.Headers[LlmHeaders.BatchId] = batchId;
        exchange.Out.Headers[LlmHeaders.BatchStatus] = status;

        // Idempotency: batch id is namespaced under conversation "batch" so
        // it does not collide with regular tool_use_id reservations.
        var reservation = await _idempotency
            .TryReserveAsync(IdempotencyConversation, batchId, exchange, ct)
            .ConfigureAwait(false);

        if (!reservation.IsNew)
        {
            exchange.Out.Headers[LlmHeaders.BatchDuplicate] = true;
            return;
        }

        // Best-effort enrichment from IBatchStore — failures must not stop the route.
        BatchJobRecord? record = null;
        if (_batchStore is not null)
        {
            try
            {
                record = await _batchStore.GetAsync(batchId, exchange, ct).ConfigureAwait(false);
                if (record is not null)
                {
                    if (!string.IsNullOrEmpty(record.ConversationId))
                        exchange.Out.Headers[LlmHeaders.ConversationId] = record.ConversationId;
                    if (!string.IsNullOrEmpty(record.ProviderId))
                        exchange.Out.Headers[LlmHeaders.ProviderId] = record.ProviderId;
                    if (!string.IsNullOrEmpty(record.ModelId))
                        exchange.Out.Headers[LlmHeaders.ModelId] = record.ModelId;
                }

                if (string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase))
                {
                    var err = ResolveErrorMessage(exchange) ?? "callback reported failure";
                    await _batchStore.MarkFailedAsync(batchId, err, exchange, ct).ConfigureAwait(false);
                }
                else
                {
                    await _batchStore.MarkCompletedAsync(batchId, exchange, ct).ConfigureAwait(false);
                }
            }
            catch
            {
                // Tracking failures are non-fatal; the callback still has to flow downstream.
            }
        }

        // Opt-in: append the assistant turn to the conversation tree on success.
        // The append is best-effort — failure here must not break the callback flow.
        if (_conversationStore is not null
            && record is { AppendToConversation: true, ConversationId: { Length: > 0 } convId }
            && !string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var assistantText = ExtractJsonField(exchange.In.Body, _options.AssistantContentJsonFields);
                if (!string.IsNullOrEmpty(assistantText))
                {
                    var path = await _conversationStore
                        .LoadPathAsync(convId, leafId: null, exchange, ct)
                        .ConfigureAwait(false);
                    var parentId = path.Count > 0 ? path[^1].Id : null;

                    var msgId = await _conversationStore.AppendAsync(
                            convId,
                            parentId,
                            LlmMessage.Assistant(assistantText),
                            new ConversationMessageMeta
                            {
                                CreatedAtUtc = DateTime.UtcNow,
                                Iteration = path.Count,
                                ProviderId = record.ProviderId,
                                ModelId = record.ModelId,
                                StopReason = LlmStopReason.EndTurn
                            },
                            exchange,
                            ct)
                        .ConfigureAwait(false);

                    exchange.Out.Headers[LlmHeaders.ConversationMessageId] = msgId;
                }
            }
            catch
            {
                // Append failures are non-fatal; the callback still has to flow downstream.
            }
        }

        exchange.Out.Headers[LlmHeaders.BatchDuplicate] = false;

        // Confirm the reservation so a retried webhook lands in the duplicate branch.
        // We persist the raw status as the "output"; downstream routes can use
        // it for diagnostic queries.
        await _idempotency
            .CompleteAsync(IdempotencyConversation, batchId, status, exchange, ct)
            .ConfigureAwait(false);
    }

    private string? ResolveBatchId(IExchange exchange)
    {
        if (exchange.In.Headers.TryGetValue(_options.BatchIdHeader, out var hv) && hv is string hs && hs.Length > 0)
            return hs;

        if (exchange.In.Headers.TryGetValue(_options.BatchIdQueryParam, out var qv) && qv is string qs && qs.Length > 0)
            return qs;

        var fromBody = ExtractJsonField(exchange.In.Body, _options.BatchIdJsonFields);
        return fromBody;
    }

    private string ResolveStatus(IExchange exchange)
    {
        if (exchange.In.Headers.TryGetValue(_options.StatusHeader, out var hv) && hv is string hs && hs.Length > 0)
            return hs;

        var fromBody = ExtractJsonField(exchange.In.Body, _options.StatusJsonFields);
        return fromBody ?? "completed";
    }

    private string? ResolveErrorMessage(IExchange exchange)
    {
        if (exchange.In.Headers.TryGetValue(_options.ErrorHeader, out var hv) && hv is string hs && hs.Length > 0)
            return hs;

        return ExtractJsonField(exchange.In.Body, _options.ErrorJsonFields);
    }

    private static string? ExtractJsonField(object? body, IReadOnlyList<string> candidates)
    {
        if (candidates.Count == 0) return null;

        var text = body switch
        {
            null => null,
            string s => s,
            byte[] bytes => System.Text.Encoding.UTF8.GetString(bytes),
            _ => body.ToString()
        };
        if (string.IsNullOrWhiteSpace(text)) return null;

        try
        {
            using var doc = JsonDocument.Parse(text);
            foreach (var candidate in candidates)
            {
                if (TryReadDotted(doc.RootElement, candidate, out var value) && !string.IsNullOrEmpty(value))
                    return value;
            }
        }
        catch (JsonException)
        {
            // Body wasn't JSON — fall through with no match.
        }

        return null;
    }

    private static bool TryReadDotted(JsonElement root, string path, out string? value)
    {
        value = null;
        if (string.IsNullOrEmpty(path)) return false;

        var current = root;
        var idx = 0;
        while (idx < path.Length)
        {
            var dot = path.IndexOf('.', idx);
            var segment = dot == -1 ? path[idx..] : path[idx..dot];

            if (current.ValueKind == JsonValueKind.Array && int.TryParse(segment, out var arrIdx))
            {
                if (arrIdx < 0 || arrIdx >= current.GetArrayLength()) return false;
                current = current[arrIdx];
            }
            else
            {
                if (current.ValueKind != JsonValueKind.Object) return false;
                if (!current.TryGetProperty(segment, out var next)) return false;
                current = next;
            }

            if (dot == -1) break;
            idx = dot + 1;
        }

        value = current.ValueKind switch
        {
            JsonValueKind.String => current.GetString(),
            JsonValueKind.Number => current.GetRawText(),
            JsonValueKind.True or JsonValueKind.False => current.GetRawText(),
            _ => null
        };
        return value is not null;
    }

    /// <summary>Conversation id used to namespace batch reservations inside the idempotency store.</summary>
    private const string IdempotencyConversation = "batch";
}

/// <summary>Configuration for <see cref="LlmCallbackProcessor"/>.</summary>
public sealed class LlmCallbackOptions
{
    /// <summary>Header carrying the batch id; default <c>"X-Batch-Id"</c>.</summary>
    public string BatchIdHeader { get; init; } = "X-Batch-Id";

    /// <summary>Query-string / generic header fallback for the batch id; default <c>"batchId"</c>.</summary>
    public string BatchIdQueryParam { get; init; } = "batchId";

    /// <summary>Dotted JSON paths consulted in order when no header / query value is present.</summary>
    public IReadOnlyList<string> BatchIdJsonFields { get; init; } =
        ["id", "batch_id", "data.id", "batch.id"];

    /// <summary>Header carrying the lifecycle status; default <c>"X-Batch-Status"</c>.</summary>
    public string StatusHeader { get; init; } = "X-Batch-Status";

    /// <summary>Dotted JSON paths consulted in order when no status header is present.</summary>
    public IReadOnlyList<string> StatusJsonFields { get; init; } =
        ["status", "processing_status", "state"];

    /// <summary>Header carrying an error message; default <c>"X-Batch-Error"</c>.</summary>
    public string ErrorHeader { get; init; } = "X-Batch-Error";

    /// <summary>Dotted JSON paths consulted in order when no error header is present.</summary>
    public IReadOnlyList<string> ErrorJsonFields { get; init; } =
        ["error", "error.message", "failure_reason"];

    /// <summary>
    /// Dotted JSON paths consulted in order to extract the assistant turn text
    /// from a successful callback body when
    /// <see cref="redb.Route.Llm.Engine.Storage.BatchJobRecord.AppendToConversation"/> is true.
    /// Defaults cover the most common provider shapes (Anthropic / OpenAI / vLLM).
    /// </summary>
    public IReadOnlyList<string> AssistantContentJsonFields { get; init; } =
        ["output_text", "choices.0.message.content", "content.0.text", "message.content", "text"];
}

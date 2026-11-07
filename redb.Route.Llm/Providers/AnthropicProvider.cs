using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using redb.Route.Llm.Abstractions.Tools;

namespace redb.Route.Llm.Providers;

/// <summary>
/// Transport for Anthropic's Messages API (POST <c>{baseUrl}/v1/messages</c>).
/// Maps <see cref="LlmRequest"/> to Anthropic's <c>messages</c>/<c>tools</c>/
/// <c>tool_use</c>/<c>tool_result</c> content-block model and parses
/// <see cref="LlmResponse"/> from the standard response envelope.
/// <para>
/// Streaming is true SSE — content blocks are reassembled from
/// <c>content_block_start</c>/<c>content_block_delta</c>/<c>content_block_stop</c>
/// events; tool-use blocks accumulate partial JSON in <c>input_json_delta</c>
/// and surface as a single complete <see cref="LlmToolUseBlock"/> at the end
/// of their block.
/// </para>
/// <para>
/// Error mapping: HTTP 429 → <see cref="LlmRateLimitException"/> (honours
/// <c>retry-after</c>); HTTP 529 ("overloaded") and HTTP 5xx →
/// <see cref="LlmTransientException"/>. Other failures surface as
/// <see cref="HttpRequestException"/>.
/// </para>
/// </summary>
public sealed class AnthropicProvider : ILlmProvider
{
    private const string AnthropicVersion = "2023-06-01";
    private static readonly Uri DefaultBaseUrl = new("https://api.anthropic.com/");

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
        // Match JsonMessageSerializer.DefaultOptions: emit Cyrillic/emoji/&/>/< as UTF-8,
        // not \uXXXX. Affects request body to Anthropic (safe — it accepts both) and the
        // re-encoded tool_use input string surfaced as LlmToolUseBlock.InputJson, which is
        // what shows up in `[SHELL-TOOL] ▶ in=…` logs.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly LlmConnectionFactory _factory;
    private readonly HttpClient _http;
    private readonly Uri _endpoint;
    private readonly ILogger? _logger;

    // The factory's model id and contract tier are fixed for this provider, so its profile is
    // resolved once; only a per-request ModelId override triggers a fresh resolve (see BuildRequestBody).
    private readonly AnthropicModelProfile _factoryProfile;

    /// <summary>Creates the provider with a connection factory (builds an internal HttpClient).</summary>
    public AnthropicProvider(LlmConnectionFactory factory)
        : this(factory, BuildDefaultClient(factory))
    {
    }

    /// <summary>Creates the provider with an externally owned HttpClient.</summary>
    public AnthropicProvider(LlmConnectionFactory factory, HttpClient http)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _logger = factory.LoggerFactory?.CreateLogger("redb.Route.Llm.AnthropicProvider");
        _factoryProfile = AnthropicModelProfile.Resolve(factory.ModelId, factory.ModelContractTier);

        var baseUrl = factory.BaseUrl ?? DefaultBaseUrl;
        _endpoint = new Uri(EnsureTrailingSlash(baseUrl), "v1/messages");
    }

    /// <inheritdoc />
    public string ProviderId => "anthropic";

    /// <inheritdoc />
    public string ModelId => _factory.ModelId;

    /// <inheritdoc />
    /// <remarks>Limited as a whole by <see cref="LlmConnectionFactory.RequestTimeoutMs"/>.</remarks>
    public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return LlmHttpTransport.WithinCallLimitAsync(_factory, ProviderId, t => CompleteCoreAsync(request, t), ct);
    }

    private async Task<LlmResponse> CompleteCoreAsync(LlmRequest request, CancellationToken ct)
    {
        var body = BuildRequestBody(request, stream: false);
        using var http = new HttpRequestMessage(HttpMethod.Post, _endpoint)
        {
            Content = JsonContent.Create(body, options: JsonOpts),
            // HttpClient's DefaultRequestVersion applies only to messages the client creates
            // itself; a hand-built message starts at HTTP/1.1. Carry the client's defaults over,
            // otherwise the HTTP/2 keep-alive pings of BuildDefaultHandler never happen.
            Version = _http.DefaultRequestVersion,
            VersionPolicy = _http.DefaultVersionPolicy,
        };
        ApplyHeaders(http);

        using var resp = await _http.SendAsync(http, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            await ThrowMappedAsync(resp, ct).ConfigureAwait(false);

        var json = await resp.Content.ReadFromJsonAsync<JsonObject>(JsonOpts, ct).ConfigureAwait(false)
                   ?? throw new InvalidOperationException("anthropic: empty response body.");

        return ParseResponse(json);
    }

    /// <summary>
    /// Anthropic SSE streaming. Pieces are yielded as they arrive: visible text as <see cref="LlmTextBlock"/>, the
    /// model's thinking as <see cref="LlmThinkingBlock"/>. The last chunk carries the completed tool calls and
    /// <see cref="LlmStreamChunk.Response"/>: the stream assembled into the message a plain call returns and read by
    /// the same parser, so thinking keeps its signature and the streamed answer cannot differ from the plain one.
    /// </summary>
    public async IAsyncEnumerable<LlmStreamChunk> StreamAsync(
        LlmRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var body = BuildRequestBody(request, stream: true);
        using var http = new HttpRequestMessage(HttpMethod.Post, _endpoint)
        {
            Content = JsonContent.Create(body, options: JsonOpts),
            // HttpClient's DefaultRequestVersion applies only to messages the client creates
            // itself; a hand-built message starts at HTTP/1.1. Carry the client's defaults over,
            // otherwise the HTTP/2 keep-alive pings of BuildDefaultHandler never happen.
            Version = _http.DefaultRequestVersion,
            VersionPolicy = _http.DefaultVersionPolicy,
        };
        http.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        ApplyHeaders(http);

        // RequestTimeoutMs covers the whole call, the stream included; StreamIdleTimeoutMs the waits on the provider.
        using var call = new LlmStreamCall(_factory, ProviderId, ct);
        using var resp = await call.SendAsync(_http, http).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            await ThrowMappedAsync(resp, ct).ConfigureAwait(false);

        using var stream = await call.OpenAsync(resp).ConfigureAwait(false);
        using var reader = new StreamReader(stream);

        // The stream is assembled into the message a plain call returns and read by the same parser, so the streamed
        // answer cannot differ from the plain one: blocks in their order, thinking with its signature, redacted
        // thinking, tool input, usage, the stop reason and the message id.
        JsonObject? message = null;
        var blocks = new SortedDictionary<int, JsonObject>();
        var toolInputs = new Dictionary<int, StringBuilder>();
        var usage = new JsonObject();
        string? rawStop = null;
        JsonNode? stopSequence = null;
        string? currentEvent = null;

        while (await call.ReadLineAsync(reader).ConfigureAwait(false) is { } line)
        {
            if (line.Length == 0)
            {
                currentEvent = null;
                continue;
            }

            if (line.StartsWith("event:", StringComparison.Ordinal))
            {
                currentEvent = line.AsSpan(6).Trim().ToString();
                continue;
            }

            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;

            var payload = line.AsSpan(5).Trim().ToString();
            if (payload.Length == 0) continue;

            var frame = LlmStreamFrames.Read(ProviderId, payload);
            var evt = currentEvent ?? LlmStreamFrames.Text(frame["type"]);

            switch (evt)
            {
                case "message_start":
                    message = frame["message"] is JsonObject started
                        ? (JsonObject)started.DeepClone()
                        : throw new InvalidOperationException(
                            $"anthropic: message_start without a message: {LlmStreamFrames.Shown(payload)}");
                    if (message["usage"] is JsonObject startUsage) Overlay(usage, startUsage);
                    break;

                case "content_block_start":
                {
                    var index = BlockIndex(frame, payload);
                    var block = frame["content_block"] is JsonObject startedBlock
                        ? (JsonObject)startedBlock.DeepClone()
                        : throw new InvalidOperationException(
                            $"anthropic: content_block_start without a block: {LlmStreamFrames.Shown(payload)}");
                    blocks[index] = block;
                    if (LlmStreamFrames.Text(block["type"]) == "tool_use") toolInputs[index] = new StringBuilder();
                    break;
                }

                case "content_block_delta":
                {
                    var index = BlockIndex(frame, payload);
                    if (!blocks.TryGetValue(index, out var block))
                        throw new InvalidOperationException(
                            $"anthropic: a delta for block {index}, which never started: {LlmStreamFrames.Shown(payload)}");

                    var delta = frame["delta"] as JsonObject;
                    switch (LlmStreamFrames.Text(delta?["type"]))
                    {
                        case "text_delta" when LlmStreamFrames.Text(delta!["text"]) is { Length: > 0 } piece:
                            block["text"] = LlmStreamFrames.Text(block["text"]) + piece;
                            yield return new LlmStreamChunk([new LlmTextBlock(piece)], StopReason: null, Usage: null);
                            break;

                        case "thinking_delta" when LlmStreamFrames.Text(delta!["thinking"]) is { Length: > 0 } piece:
                            block["thinking"] = LlmStreamFrames.Text(block["thinking"]) + piece;
                            yield return new LlmStreamChunk([new LlmThinkingBlock(piece)], StopReason: null, Usage: null);
                            break;

                        case "signature_delta":
                            block["signature"] = LlmStreamFrames.Text(block["signature"]) + LlmStreamFrames.Text(delta!["signature"]);
                            break;

                        case "input_json_delta":
                            if (!toolInputs.TryGetValue(index, out var input))
                                throw new InvalidOperationException(
                                    $"anthropic: tool input for block {index}, which is not a tool call: {LlmStreamFrames.Shown(payload)}");
                            input.Append(LlmStreamFrames.Text(delta!["partial_json"]));
                            break;

                        // Other deltas (citations_delta and the like) carry what the plain parser does not read either.
                    }
                    break;
                }

                case "content_block_stop":
                    FinishToolInput(blocks, toolInputs, BlockIndex(frame, payload));
                    break;

                case "message_delta":
                    if (frame["delta"] is JsonObject messageDelta)
                    {
                        if (LlmStreamFrames.Text(messageDelta["stop_reason"]) is { Length: > 0 } stopReason)
                            rawStop = stopReason;
                        stopSequence = messageDelta["stop_sequence"]?.DeepClone();
                    }
                    // Running counts: what message_delta reports replaces what message_start did.
                    if (frame["usage"] is JsonObject deltaUsage) Overlay(usage, deltaUsage);
                    break;

                case "error":
                {
                    var err = frame["error"] as JsonObject;
                    var type = err?["type"]?.GetValue<string>() ?? "unknown";
                    var errorMessage = err?["message"]?.GetValue<string>() ?? "stream error";
                    if (type == "overloaded_error")
                        throw new LlmTransientException(ProviderId, $"anthropic SSE overloaded: {errorMessage}");
                    if (type == "rate_limit_error")
                        throw new LlmRateLimitException(ProviderId, $"anthropic SSE rate limit: {errorMessage}");
                    throw new HttpRequestException($"anthropic SSE error ({type}): {errorMessage}");
                }

                // message_stop and ping carry nothing to take. Event types this reader does not know are skipped:
                // Anthropic adds new ones and documents that clients ignore those they do not know.
            }
        }

        foreach (var index in toolInputs.Keys.ToList())
            FinishToolInput(blocks, toolInputs, index);

        // Without a stop_reason the model never said it was done: the connection ended mid-answer, and handing the
        // pieces on as a finished answer would store a cut one.
        if (message is null || rawStop is null)
            throw new InvalidOperationException(
                "anthropic: the stream ended before the model said why it stopped (no stop_reason); the answer is cut.");

        var content = new JsonArray();
        foreach (var block in blocks.Values)
            content.Add(block);
        message["content"] = content;
        message["stop_reason"] = rawStop;
        message["stop_sequence"] = stopSequence;
        message["usage"] = usage;

        var response = ParseResponse(message);
        yield return new LlmStreamChunk(
            [.. response.Content.OfType<LlmToolUseBlock>()],
            response.StopReason,
            response.Usage)
        {
            Response = response
        };
    }

    private static int BlockIndex(JsonObject frame, string payload) =>
        frame["index"] is JsonValue value && value.TryGetValue<int>(out var index)
            ? index
            : throw new InvalidOperationException(
                $"anthropic: a block event without an index: {LlmStreamFrames.Shown(payload)}");

    /// <summary>Puts the streamed input of a finished tool block in place of the start block's empty one.</summary>
    private static void FinishToolInput(
        SortedDictionary<int, JsonObject> blocks, Dictionary<int, StringBuilder> inputs, int index)
    {
        if (!inputs.Remove(index, out var input) || input.Length == 0) return;

        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(input.ToString());
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"anthropic: the streamed input of tool block {index} is not JSON: {LlmStreamFrames.Shown(input.ToString())}", ex);
        }

        blocks[index]["input"] = parsed;
    }

    private static void Overlay(JsonObject target, JsonObject source)
    {
        foreach (var (key, value) in source)
            target[key] = value?.DeepClone();
    }

    /// <summary>The package's default client for model calls: HTTP/2 with keep-alive pings (<see cref="LlmHttpTransport"/>).</summary>
    internal static HttpClient BuildDefaultClient(LlmConnectionFactory factory) => LlmHttpTransport.BuildClient(factory);

    /// <summary>Transport of the default client (<see cref="LlmHttpTransport.BuildHandler"/>).</summary>
    internal static SocketsHttpHandler BuildDefaultHandler() => LlmHttpTransport.BuildHandler();

    private void ApplyHeaders(HttpRequestMessage http)
    {
        var key = _factory.ApiKey;
        if (!string.IsNullOrWhiteSpace(key))
            http.Headers.TryAddWithoutValidation("x-api-key", key);
        http.Headers.TryAddWithoutValidation("anthropic-version", AnthropicVersion);
    }

    private JsonObject BuildRequestBody(LlmRequest request, bool stream)
    {
        var model = request.ModelId ?? _factory.ModelId;
        var body = new JsonObject
        {
            ["model"] = model,
            // Anthropic requires max_tokens; default conservatively when caller omits it.
            ["max_tokens"] = request.MaxTokens ?? _factory.MaxTokens ?? 1024,
            ["messages"] = BuildMessages(request)
        };

        if (!string.IsNullOrWhiteSpace(request.SystemPrompt))
        {
            // A plain string is the ordinary shape. Caching needs the block form, because
            // cache_control is a property OF a content block — there is nowhere to hang it on a
            // bare string. One block, marked: the provider caches everything rendered up to that
            // point, which is tools + system, and later turns re-read it instead of re-paying.
            body["system"] = request.CacheSystemPrompt
                ? new JsonArray(new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = request.SystemPrompt,
                    ["cache_control"] = new JsonObject { ["type"] = "ephemeral" },
                })
                : request.SystemPrompt;
        }

        // The sampling knobs (temperature/top_p) changed contract across Anthropic
        // generations: Claude 3.x accepts both, Claude 4.0–4.6 accepts at most one (both
        // → 400), Claude 4.7+/5 reject non-default sampling entirely (→ 400). Shape the
        // request by the target model's policy so we never send a field it will reject;
        // unknown ids default to the modern (no-sampling) contract — see AnthropicModelProfile.
        // Reuse the factory profile unless the request overrides the model id.
        var profile = (request.ModelId is null || request.ModelId == _factory.ModelId)
            ? _factoryProfile
            : AnthropicModelProfile.Resolve(model, _factory.ModelContractTier);
        ApplySampling(body, request, model, profile);

        // Effort ladder (Claude 4.6+ / Sonnet 5 / Opus 5): output_config.effort. Only when the
        // factory asks for it — the field is unknown to older models and to Haiku 4.5.
        if (!string.IsNullOrWhiteSpace(_factory.Effort))
            body["output_config"] = new JsonObject { ["effort"] = _factory.Effort.Trim() };

        if (request.StopSequences is { Count: > 0 } stops)
        {
            var arr = new JsonArray();
            foreach (var s in stops) arr.Add(s);
            body["stop_sequences"] = arr;
        }

        if (request.Tools.Count > 0)
        {
            var tools = new JsonArray();
            foreach (var cap in request.Tools)
                tools.Add(MapTool(cap));
            body["tools"] = tools;
        }

        if (stream) body["stream"] = true;
        return body;
    }

    /// <summary>
    /// Writes <c>temperature</c>/<c>top_p</c> onto the request body according to the model's
    /// <see cref="AnthropicSamplingPolicy"/>: both when allowed, a single preferred knob on
    /// 4.0–4.6 (temperature wins), or neither on 4.7+/5. Anything dropped that the caller set
    /// leaves an <c>llm.sampling.dropped</c> breadcrumb on the current activity so
    /// "why is temperature ignored?" is answerable.
    /// </summary>
    private void ApplySampling(JsonObject body, LlmRequest request, string model, AnthropicModelProfile profile)
    {
        switch (profile.Sampling)
        {
            case AnthropicSamplingPolicy.Both:
                if (request.Temperature is { } t0) body["temperature"] = t0;
                if (request.TopP is { } p0) body["top_p"] = p0;
                break;

            case AnthropicSamplingPolicy.AtMostOne:
                // Both knobs together 400 on Claude 4.0–4.6; prefer temperature and drop top_p.
                if (request.Temperature is { } t1)
                {
                    body["temperature"] = t1;
                    if (request.TopP is not null)
                        NoteDropped(model, profile, "top_p", "accepts at most one sampling knob");
                }
                else if (request.TopP is { } p1)
                {
                    body["top_p"] = p1;
                }
                break;

            case AnthropicSamplingPolicy.None:
                if (request.Temperature is not null && request.TopP is not null)
                    NoteDropped(model, profile, "temperature,top_p", "sampling removed on this model");
                else if (request.Temperature is not null)
                    NoteDropped(model, profile, "temperature", "sampling removed on this model");
                else if (request.TopP is not null)
                    NoteDropped(model, profile, "top_p", "sampling removed on this model");
                break;
        }
    }

    private void NoteDropped(string model, AnthropicModelProfile profile, string dropped, string reason)
    {
        // Warn on the logger (when wired) and leave a span event either way, so a dropped knob
        // is answerable via logs and traces — never silent.
        _logger?.LogWarning(
            "anthropic: model '{Model}' ({Tier}) {Reason}; dropped {Dropped}. " +
            "Steer via the system prompt, or set LlmConnectionFactory.ModelContractTier to override.",
            model, profile.Tier, reason, dropped);

        System.Diagnostics.Activity.Current?.AddEvent(new System.Diagnostics.ActivityEvent(
            "llm.sampling.dropped",
            tags: new System.Diagnostics.ActivityTagsCollection
            {
                ["llm.model.id"] = model,
                ["llm.model.tier"] = profile.Tier.ToString(),
                ["llm.sampling.policy"] = profile.Sampling.ToString(),
                ["llm.sampling.dropped_params"] = dropped,
                ["llm.sampling.reason"] = reason
            }));
    }

    /// <summary>True for a request block of kind thinking or redacted_thinking.</summary>
    private static bool IsThinkingBlock(JsonNode? block) =>
        block?["type"]?.GetValue<string>() is "thinking" or "redacted_thinking";

    private static JsonArray BuildMessages(LlmRequest request)
    {
        var arr = new JsonArray();
        foreach (var m in request.Messages)
        {
            // Anthropic accepts only "user" and "assistant". Tool results are
            // user-role messages containing tool_result blocks (Anthropic's own
            // convention — no normalisation needed here).
            var role = m.Role == "assistant" ? "assistant" : "user";
            var content = new JsonArray();

            foreach (var b in m.Content)
            {
                switch (b)
                {
                    // Whitespace-only text is rejected by the API the same way as empty text
                    // ("text content blocks must be non-empty"), so it is dropped here too.
                    case LlmTextBlock tb when !string.IsNullOrWhiteSpace(tb.Text):
                        content.Add(new JsonObject
                        {
                            ["type"] = "text",
                            ["text"] = tb.Text
                        });
                        break;

                    // A thinking block goes back exactly as it arrived: redacted thinking as its data
                    // payload, plain thinking as text plus the signature. The wire has no form for an
                    // unsigned block (another provider's reasoning after a provider switch): a thinking
                    // block without a signature is rejected, so it is not written into the request.
                    case LlmThinkingBlock th when th.IsRedacted:
                        content.Add(new JsonObject
                        {
                            ["type"] = "redacted_thinking",
                            ["data"] = th.RedactedData
                        });
                        break;

                    case LlmThinkingBlock th when !string.IsNullOrEmpty(th.Signature):
                        content.Add(new JsonObject
                        {
                            ["type"] = "thinking",
                            ["thinking"] = th.Text,
                            ["signature"] = th.Signature
                        });
                        break;

                    case LlmToolUseBlock tu:
                        content.Add(new JsonObject
                        {
                            ["type"] = "tool_use",
                            ["id"] = tu.ToolUseId,
                            ["name"] = tu.Name,
                            ["input"] = ParseJsonOrEmpty(tu.InputJson)
                        });
                        break;

                    case LlmToolResultBlock tr:
                        var trBlock = new JsonObject
                        {
                            ["type"] = "tool_result",
                            ["tool_use_id"] = tr.ToolUseId,
                            ["content"] = tr.OutputJson
                        };
                        if (tr.IsError) trBlock["is_error"] = true;
                        content.Add(trBlock);
                        break;
                }
            }

            // A message with nothing to continue from is not sent at all: no block left, or nothing
            // but thinking (a turn the model spent entirely on thinking, kept in the record). An
            // empty reply used to become an empty text block, and the API rejects those with 400
            // "text content blocks must be non-empty" — so ONE empty assistant reply persisted in a
            // conversation made every later call of that conversation fail until the history was
            // edited by hand (2026-09-07). A lone thinking block is not put in front of the API
            // either. Skipping is safe: the API merges consecutive same-role turns.
            if (content.All(IsThinkingBlock))
                continue;

            // A message-level breakpoint lands on the message's LAST block: cache_control is a
            // property of a content block, and the cache prefix runs up to the marked block
            // inclusive.
            if (m.CacheBreakpoint)
                content[^1]!.AsObject()["cache_control"] = new JsonObject { ["type"] = "ephemeral" };

            arr.Add(new JsonObject
            {
                ["role"] = role,
                ["content"] = content
            });
        }

        // The API also rejects an empty messages array; that can only happen when every
        // message was empty, and then the honest request is a single empty-handed user turn.
        if (arr.Count == 0)
        {
            arr.Add(new JsonObject
            {
                ["role"] = "user",
                ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "…" })
            });
        }

        return arr;
    }

    /// <summary>
    /// Projects a capability into the provider's <c>tools[]</c> entry. Only the name, the description
    /// and the input schema travel: <see cref="LlmToolSafety"/> is server-side metadata and never
    /// appears on the wire.
    /// </summary>
    private static JsonObject MapTool(LlmToolCapability cap)
    {
        var schema = ParseJsonOrEmpty(cap.InputSchema) as JsonObject
                     ?? new JsonObject { ["type"] = "object" };
        return new JsonObject
        {
            ["name"] = cap.Name,
            ["description"] = cap.Description,
            ["input_schema"] = schema
        };
    }

    private static JsonNode ParseJsonOrEmpty(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new JsonObject();
        try { return JsonNode.Parse(json) ?? new JsonObject(); }
        catch (JsonException) { return new JsonObject(); }
    }

    private static LlmResponse ParseResponse(JsonObject json)
    {
        var blocks = new List<LlmContentBlock>();
        if (json["content"] is JsonArray content)
        {
            foreach (var node in content)
            {
                if (node is not JsonObject block) continue;
                var type = block["type"]?.GetValue<string>();
                switch (type)
                {
                    case "text":
                        var text = block["text"]?.GetValue<string>() ?? string.Empty;
                        blocks.Add(new LlmTextBlock(text));
                        break;
                    // The model's thinking comes back signed, and the signature is what the API
                    // verifies on the way in — so the block travels on untouched, text and signature
                    // together, into the next request of the same run.
                    case "thinking":
                        var thinkingText = block["thinking"]?.GetValue<string>() ?? string.Empty;
                        var signature = block["signature"]?.GetValue<string>();
                        blocks.Add(new LlmThinkingBlock(thinkingText, signature));
                        break;
                    // redacted_thinking: the model reasoned, the API withheld the text and returned an
                    // opaque payload instead. There is nothing to read — only something to hand back.
                    case "redacted_thinking":
                        blocks.Add(new LlmThinkingBlock(
                            string.Empty,
                            RedactedData: block["data"]?.GetValue<string>()));
                        break;
                    case "tool_use":
                        var id = block["id"]?.GetValue<string>() ?? Guid.NewGuid().ToString("N");
                        var name = block["name"]?.GetValue<string>() ?? string.Empty;
                        var input = block["input"]?.ToJsonString(JsonOpts) ?? "{}";
                        blocks.Add(new LlmToolUseBlock(id, name, input));
                        break;
                }
            }
        }

        if (blocks.Count == 0) blocks.Add(new LlmTextBlock(string.Empty));

        var rawStop = json["stop_reason"]?.GetValue<string>();
        var stop = MapStopReason(rawStop);

        var usage = LlmUsage.Empty;
        if (json["usage"] is JsonObject u)
        {
            var inT = u["input_tokens"]?.GetValue<int>() ?? 0;
            var outT = u["output_tokens"]?.GetValue<int>() ?? 0;
            usage = new LlmUsage(
                inT,
                outT,
                u["cache_creation_input_tokens"]?.GetValue<int>() ?? 0,
                u["cache_read_input_tokens"]?.GetValue<int>() ?? 0);
        }

        return new LlmResponse
        {
            Content = blocks,
            StopReason = stop,
            Usage = usage,
            RawStopReason = rawStop,
            ProviderResponseId = json["id"]?.GetValue<string?>()
        };
    }

    private static LlmStopReason MapStopReason(string? raw) => raw switch
    {
        "end_turn" => LlmStopReason.EndTurn,
        "tool_use" => LlmStopReason.ToolUse,
        "max_tokens" => LlmStopReason.MaxTokens,
        "stop_sequence" => LlmStopReason.StopSequence,
        _ => LlmStopReason.Other
    };

    private async Task ThrowMappedAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        var raw = await SafeReadAsync(resp, ct).ConfigureAwait(false);
        var summary = $"anthropic: {(int)resp.StatusCode} {resp.ReasonPhrase} from {_endpoint}";

        // The mapping itself moved to LlmHttpErrors so the OpenAI family can share it — two
        // copies of "which status means retry" drift apart, and the drift is invisible until
        // production. Behaviour here is unchanged, including the soft "overloaded" 529.
        throw LlmHttpErrors.FromResponse(ProviderId, resp, $"{summary}. Body: {raw}", raw);
    }

    private static Uri EnsureTrailingSlash(Uri uri)
    {
        var s = uri.ToString();
        return s.EndsWith('/') ? uri : new Uri(s + "/");
    }

    private static async Task<string> SafeReadAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        try { return await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false); }
        catch { return "<unreadable>"; }
    }
}

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
    public async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

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

    /// <inheritdoc />
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

        using var resp = await _http.SendAsync(http, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            await ThrowMappedAsync(resp, ct).ConfigureAwait(false);

        using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream);

        var blocks = new SortedDictionary<int, BlockAccumulator>();
        LlmStopReason? finalStop = null;
        string? rawStop = null;
        int inputTokens = 0;
        int outputTokens = 0;
        int cacheWriteTokens = 0;
        int cacheReadTokens = 0;

        string? currentEvent = null;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null) break;

            if (line.Length == 0)
            {
                currentEvent = null;
                continue;
            }

            if (line.StartsWith("event:", StringComparison.Ordinal))
            {
                currentEvent = line.AsSpan(6).TrimStart().ToString();
                continue;
            }

            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;

            var payload = line.AsSpan(5).TrimStart().ToString();
            if (payload.Length == 0) continue;

            JsonObject? frame;
            try { frame = JsonNode.Parse(payload) as JsonObject; }
            catch (JsonException) { continue; }
            if (frame is null) continue;

            var evt = currentEvent ?? frame["type"]?.GetValue<string>();

            switch (evt)
            {
                case "message_start":
                    if (frame["message"] is JsonObject msg
                        && msg["usage"] is JsonObject uStart)
                    {
                        inputTokens = uStart["input_tokens"]?.GetValue<int>() ?? 0;
                        outputTokens = uStart["output_tokens"]?.GetValue<int>() ?? 0;
                        cacheWriteTokens = uStart["cache_creation_input_tokens"]?.GetValue<int>() ?? 0;
                        cacheReadTokens = uStart["cache_read_input_tokens"]?.GetValue<int>() ?? 0;
                    }
                    break;

                case "content_block_start":
                {
                    var idx = frame["index"]?.GetValue<int>() ?? 0;
                    var block = frame["content_block"] as JsonObject;
                    var kind = block?["type"]?.GetValue<string>() ?? "text";
                    var acc = new BlockAccumulator { Kind = kind };
                    if (kind == "tool_use")
                    {
                        acc.ToolUseId = block?["id"]?.GetValue<string>();
                        acc.ToolName = block?["name"]?.GetValue<string>();
                    }
                    blocks[idx] = acc;
                    break;
                }

                case "content_block_delta":
                {
                    var idx = frame["index"]?.GetValue<int>() ?? 0;
                    if (!blocks.TryGetValue(idx, out var acc)) break;
                    var delta = frame["delta"] as JsonObject;
                    var dType = delta?["type"]?.GetValue<string>();

                    if (dType == "text_delta"
                        && delta?["text"]?.GetValue<string>() is { Length: > 0 } textDelta)
                    {
                        acc.Buffer.Append(textDelta);
                        yield return new LlmStreamChunk(
                            [new LlmTextBlock(textDelta)], StopReason: null, Usage: null);
                    }
                    else if (dType == "input_json_delta"
                             && delta?["partial_json"]?.GetValue<string>() is { } partial)
                    {
                        acc.Buffer.Append(partial);
                    }
                    break;
                }

                case "content_block_stop":
                    // Block is complete — we emit text deltas live and reassemble
                    // tool_use blocks at message_stop so they surface as one block.
                    break;

                case "message_delta":
                    if (frame["delta"] is JsonObject mdDelta)
                    {
                        if (mdDelta["stop_reason"]?.GetValue<string>() is { Length: > 0 } sr)
                        {
                            rawStop = sr;
                            finalStop = MapStopReason(sr);
                        }
                    }
                    if (frame["usage"] is JsonObject mdU)
                    {
                        // message_delta carries the running output_tokens count.
                        outputTokens = mdU["output_tokens"]?.GetValue<int>() ?? outputTokens;
                    }
                    break;

                case "message_stop":
                    // Loop will terminate at end-of-stream.
                    break;

                case "error":
                {
                    var err = frame["error"] as JsonObject;
                    var type = err?["type"]?.GetValue<string>() ?? "unknown";
                    var message = err?["message"]?.GetValue<string>() ?? "stream error";
                    if (type == "overloaded_error")
                        throw new LlmTransientException(ProviderId, $"anthropic SSE overloaded: {message}");
                    if (type == "rate_limit_error")
                        throw new LlmRateLimitException(ProviderId, $"anthropic SSE rate limit: {message}");
                    throw new HttpRequestException($"anthropic SSE error ({type}): {message}");
                }
            }
        }

        // Final chunk: completed tool_use blocks + stop reason + usage.
        var finalBlocks = new List<LlmContentBlock>();
        foreach (var (_, acc) in blocks)
        {
            if (acc.Kind != "tool_use") continue;
            finalBlocks.Add(new LlmToolUseBlock(
                acc.ToolUseId ?? Guid.NewGuid().ToString("N"),
                acc.ToolName ?? string.Empty,
                acc.Buffer.Length == 0 ? "{}" : acc.Buffer.ToString()));
        }

        yield return new LlmStreamChunk(
            finalBlocks,
            finalStop ?? LlmStopReason.EndTurn,
            new LlmUsage(inputTokens, outputTokens, cacheWriteTokens, cacheReadTokens));
    }

    private sealed class BlockAccumulator
    {
        public string Kind { get; set; } = "text";
        public string? ToolUseId { get; set; }
        public string? ToolName { get; set; }
        public StringBuilder Buffer { get; } = new();
    }

    internal static HttpClient BuildDefaultClient(LlmConnectionFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        return new HttpClient(BuildDefaultHandler())
        {
            Timeout = TimeSpan.FromMilliseconds(Math.Max(1000, factory.RequestTimeoutMs)),
            // HTTP/2 when the server offers it (api.anthropic.com does), so the keep-alive pings
            // below have a frame to ride on; HTTP/1.1 stays the fallback.
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        };
    }

    /// <summary>
    /// Transport of the default client. A non-streaming completion is one POST whose response
    /// arrives only when the model has finished — tens of seconds during which NOTHING crosses the
    /// wire. Middleboxes (VPN tunnels, NAT, corporate proxies) routinely drop a TLS connection that
    /// has been silent for ~50 s, and the caller then sees "The response ended prematurely" at
    /// exactly that mark: a long answer never arrives, and no retry helps because the retry is
    /// just as long. HTTP/2 PING frames sent while a request is in flight keep the connection
    /// visibly alive without touching the request itself; on HTTP/1.1 they are simply not sent.
    /// Measured 2026-09-04 through a sing-tun/xray tunnel: 45 s of silence survived, 55 s was
    /// cut; with a ping every 15 s four consecutive 50-second waits all completed.
    /// </summary>
    internal static SocketsHttpHandler BuildDefaultHandler() => new()
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        KeepAlivePingDelay = TimeSpan.FromSeconds(15),
        KeepAlivePingTimeout = TimeSpan.FromSeconds(20),
        KeepAlivePingPolicy = HttpKeepAlivePingPolicy.WithActiveRequests,
    };

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

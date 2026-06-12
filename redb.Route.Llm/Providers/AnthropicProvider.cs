using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
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
            Content = JsonContent.Create(body, options: JsonOpts)
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
            Content = JsonContent.Create(body, options: JsonOpts)
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
            new LlmUsage(inputTokens, outputTokens));
    }

    private sealed class BlockAccumulator
    {
        public string Kind { get; set; } = "text";
        public string? ToolUseId { get; set; }
        public string? ToolName { get; set; }
        public StringBuilder Buffer { get; } = new();
    }

    private static HttpClient BuildDefaultClient(LlmConnectionFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        return new HttpClient
        {
            Timeout = TimeSpan.FromMilliseconds(Math.Max(1000, factory.RequestTimeoutMs))
        };
    }

    private void ApplyHeaders(HttpRequestMessage http)
    {
        var key = _factory.ApiKey;
        if (!string.IsNullOrWhiteSpace(key))
            http.Headers.TryAddWithoutValidation("x-api-key", key);
        http.Headers.TryAddWithoutValidation("anthropic-version", AnthropicVersion);
    }

    private JsonObject BuildRequestBody(LlmRequest request, bool stream)
    {
        var body = new JsonObject
        {
            ["model"] = request.ModelId ?? _factory.ModelId,
            // Anthropic requires max_tokens; default conservatively when caller omits it.
            ["max_tokens"] = request.MaxTokens ?? _factory.MaxTokens ?? 1024,
            ["messages"] = BuildMessages(request)
        };

        if (!string.IsNullOrWhiteSpace(request.SystemPrompt))
            body["system"] = request.SystemPrompt;

        if (request.Temperature is { } t) body["temperature"] = t;
        if (request.TopP is { } tp) body["top_p"] = tp;

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
                    case LlmTextBlock tb when !string.IsNullOrEmpty(tb.Text):
                        content.Add(new JsonObject
                        {
                            ["type"] = "text",
                            ["text"] = tb.Text
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

            if (content.Count == 0)
                content.Add(new JsonObject { ["type"] = "text", ["text"] = string.Empty });

            arr.Add(new JsonObject
            {
                ["role"] = role,
                ["content"] = content
            });
        }
        return arr;
    }

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
            usage = new LlmUsage(inT, outT);
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
        var status = (int)resp.StatusCode;
        var summary = $"anthropic: {status} {resp.ReasonPhrase} from {_endpoint}";

        if (status == 429)
        {
            TimeSpan? retryAfter = null;
            if (resp.Headers.RetryAfter is { } ra)
            {
                if (ra.Delta is { } d) retryAfter = d;
                else if (ra.Date is { } when) retryAfter = when - DateTimeOffset.UtcNow;
            }
            throw new LlmRateLimitException(ProviderId, $"{summary}. Body: {raw}", retryAfter, raw);
        }

        // Anthropic returns 529 for the soft "overloaded" signal.
        if (status == 529 || (status >= 500 && status <= 599))
            throw new LlmTransientException(ProviderId, $"{summary}. Body: {raw}", status, raw);

        throw new HttpRequestException($"{summary}. Body: {raw}", inner: null,
            statusCode: resp.StatusCode);
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

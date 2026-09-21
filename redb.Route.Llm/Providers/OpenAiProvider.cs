using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using redb.Route.Llm.Abstractions.Tools;

namespace redb.Route.Llm.Providers;

/// <summary>
/// Transport for any OpenAI-compatible Chat Completions endpoint
/// (POST <c>{baseUrl}/chat/completions</c>).
/// <para>
/// One implementation, many providers. By switching <see cref="LlmConnectionFactory.BaseUrl"/>
/// + <see cref="LlmConnectionFactory.ApiKey"/> + <see cref="LlmConnectionFactory.ModelId"/>
/// the same provider talks to OpenAI, Groq, Cerebras, OpenRouter, Google Gemini
/// (compat mode), GitHub Models, Mistral, Together, HuggingFace router, DeepSeek,
/// xAI Grok, LM Studio, vLLM, llama.cpp server and Ollama.
/// </para>
/// <para>
/// Maps <see cref="LlmRequest"/> ↔ OpenAI <c>messages</c>/<c>tools</c>/<c>tool_calls</c>
/// and <see cref="LlmResponse"/> ↔ <c>choices[0].message</c> + <c>finish_reason</c>
/// + <c>usage</c>. Streaming falls back to the buffered default of
/// <see cref="ILlmProvider.StreamAsync"/>.
/// </para>
/// </summary>
public sealed class OpenAiProvider : ILlmProvider
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
        // Send prompts as UTF-8, not \uXXXX (matches AnthropicProvider) — smaller
        // request body and no needless escaping of non-ASCII content.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly LlmConnectionFactory _factory;
    private readonly HttpClient _http;
    private readonly Uri _endpoint;
    private readonly string _providerId;

    /// <summary>Creates the provider with an externally owned <paramref name="http"/>.</summary>
    public OpenAiProvider(LlmConnectionFactory factory, HttpClient http)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _http = http ?? throw new ArgumentNullException(nameof(http));

        var baseUrl = factory.BaseUrl ?? ResolveDefaultBaseUrl(factory.Provider);
        _endpoint = new Uri(EnsureTrailingSlash(baseUrl), "chat/completions");
        _providerId = string.IsNullOrWhiteSpace(factory.Provider) ? "openai" : factory.Provider!.ToLowerInvariant();
    }

    /// <summary>
    /// Convenience constructor that builds the package's default client: the factory timeout, HTTP/2 with keep-alive
    /// pings while a request is in flight (a thinking model's call is minutes of silence on the wire).
    /// </summary>
    public static OpenAiProvider Create(LlmConnectionFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        return new OpenAiProvider(factory, LlmHttpTransport.BuildClient(factory));
    }

    /// <inheritdoc />
    public string ProviderId => _providerId;

    /// <summary>The client this provider sends through (tests read its transport defaults).</summary>
    internal HttpClient Http => _http;

    /// <inheritdoc />
    public string ModelId => _factory.ModelId;

    /// <summary>Default base URL applied when <see cref="LlmConnectionFactory.BaseUrl"/> is null.</summary>
    public static Uri ResolveDefaultBaseUrl(string? providerKey) => (providerKey ?? "openai").ToLowerInvariant() switch
    {
        "openai" => new("https://api.openai.com/v1/"),
        "anthropic" or "claude" => new("https://api.anthropic.com/v1/"),
        "groq" => new("https://api.groq.com/openai/v1/"),
        "cerebras" => new("https://api.cerebras.ai/v1/"),
        "openrouter" => new("https://openrouter.ai/api/v1/"),
        "gemini" or "google" => new("https://generativelanguage.googleapis.com/v1beta/openai/"),
        "github-models" or "github" => new("https://models.inference.ai.azure.com/"),
        "mistral" => new("https://api.mistral.ai/v1/"),
        "together" or "togetherai" => new("https://api.together.xyz/v1/"),
        "huggingface" or "hf" => new("https://router.huggingface.co/v1/"),
        // DeepSeek's documented base is the ROOT, unlike everyone else's /v1 (checked 2026-09-10
        // against api-docs.deepseek.com after the V4.1-Flash release; /v1 still answers as a
        // legacy alias but vanished from the docs, so the canonical form is the durable one).
        "deepseek" => new("https://api.deepseek.com/"),
        "grok" or "xai" => new("https://api.x.ai/v1/"),
        "ollama" => new("http://localhost:11434/v1/"),
        "lmstudio" => new("http://localhost:1234/v1/"),
        _ => new("https://api.openai.com/v1/")
    };

    /// <inheritdoc />
    /// <remarks>Limited as a whole by <see cref="LlmConnectionFactory.RequestTimeoutMs"/>.</remarks>
    public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return LlmHttpTransport.WithinCallLimitAsync(_factory, _providerId, t => CompleteCoreAsync(request, t), ct);
    }

    private async Task<LlmResponse> CompleteCoreAsync(LlmRequest request, CancellationToken ct)
    {
        var body = BuildRequestBody(request, stream: false);
        using var http = LlmHttpTransport.NewRequest(_http, HttpMethod.Post, _endpoint);
        http.Content = JsonContent.Create(body, options: JsonOpts);
        ApplyAuthHeaders(http);

        using var resp = await _http.SendAsync(http, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            var raw = await SafeReadAsync(resp, ct).ConfigureAwait(false);
            throw LlmHttpErrors.FromResponse(
                _providerId, resp,
                $"{_providerId}: {(int)resp.StatusCode} {resp.ReasonPhrase} from {_endpoint}. Body: {raw}",
                raw);
        }

        var json = await resp.Content.ReadFromJsonAsync<JsonObject>(JsonOpts, ct).ConfigureAwait(false)
                   ?? throw new InvalidOperationException($"{_providerId}: empty response body.");

        return ParseResponse(json);
    }

    /// <summary>
    /// True OpenAI SSE streaming. Pieces are yielded as they arrive: visible text as <see cref="LlmTextBlock"/>, the
    /// model's reasoning (<c>reasoning_content</c>, <c>reasoning</c>) as <see cref="LlmThinkingBlock"/>. The last chunk
    /// carries the completed tool calls and <see cref="LlmStreamChunk.Response"/>: the stream assembled into the JSON a
    /// plain call returns and read by the same parser, so the streamed answer cannot differ from the plain one.
    /// </summary>
    public async IAsyncEnumerable<LlmStreamChunk> StreamAsync(
        LlmRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var body = BuildRequestBody(request, stream: true);
        body["stream_options"] = new JsonObject { ["include_usage"] = true };

        using var http = LlmHttpTransport.NewRequest(_http, HttpMethod.Post, _endpoint);
        http.Content = JsonContent.Create(body, options: JsonOpts);
        http.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        ApplyAuthHeaders(http);

        // RequestTimeoutMs covers the whole call, the stream included; StreamIdleTimeoutMs the waits on the provider.
        using var call = new LlmStreamCall(_factory, _providerId, ct);
        using var resp = await call.SendAsync(_http, http).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            var raw = await SafeReadAsync(resp, ct).ConfigureAwait(false);
            throw LlmHttpErrors.FromResponse(
                _providerId, resp,
                $"{_providerId}: {(int)resp.StatusCode} {resp.ReasonPhrase} from {_endpoint}. Body: {raw}",
                raw);
        }

        using var stream = await call.OpenAsync(resp).ConfigureAwait(false);
        using var reader = new StreamReader(stream);

        var content = new System.Text.StringBuilder();
        var reasoningContent = new System.Text.StringBuilder();
        var reasoning = new System.Text.StringBuilder();
        var toolCalls = new SortedDictionary<int, ToolCallAccumulator>();
        string? responseId = null;
        string? fingerprint = null;
        string? rawStop = null;
        JsonObject? usage = null;

        while (await call.ReadLineAsync(reader).ConfigureAwait(false) is { } line)
        {
            // Blank lines end an event, ':' lines are comments (keep-alives), 'event:' / 'id:' carry nothing here.
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;

            var payload = line.AsSpan(5).Trim().ToString();
            if (payload.Length == 0) continue;
            if (payload == "[DONE]") break;

            var frame = LlmStreamFrames.Read(_providerId, payload);
            if (frame["error"] is JsonObject error)
                throw new HttpRequestException(
                    $"{_providerId}: the stream reported an error: {LlmStreamFrames.Shown(error.ToJsonString())}");

            responseId ??= LlmStreamFrames.Text(frame["id"]);
            fingerprint ??= LlmStreamFrames.Text(frame["system_fingerprint"]);

            // Usage comes on a terminal usage-only frame (no choices); kept whole, so the plain parser reads it.
            if (frame["usage"] is JsonObject frameUsage)
                usage = (JsonObject)frameUsage.DeepClone();

            if (frame["choices"] is not JsonArray { Count: > 0 } choices || choices[0] is not JsonObject choice) continue;

            var delta = choice["delta"] as JsonObject;
            var emitted = new List<LlmContentBlock>();

            if (delta is not null)
            {
                // Reasoning before text, the order a plain answer lists them in.
                if (LlmStreamFrames.Text(delta["reasoning_content"]) is { Length: > 0 } reasoningPiece)
                {
                    reasoningContent.Append(reasoningPiece);
                    emitted.Add(new LlmThinkingBlock(reasoningPiece));
                }

                if (LlmStreamFrames.Text(delta["reasoning"]) is { Length: > 0 } otherReasoningPiece)
                {
                    reasoning.Append(otherReasoningPiece);
                    emitted.Add(new LlmThinkingBlock(otherReasoningPiece));
                }

                if (LlmStreamFrames.Text(delta["content"]) is { Length: > 0 } deltaText)
                {
                    content.Append(deltaText);
                    emitted.Add(new LlmTextBlock(deltaText));
                }

                if (delta["tool_calls"] is JsonArray tcArr)
                {
                    foreach (var tc in tcArr)
                    {
                        if (tc is not JsonObject tcObj) continue;
                        var idx = tcObj["index"]?.GetValue<int>() ?? 0;
                        if (!toolCalls.TryGetValue(idx, out var acc))
                            toolCalls[idx] = acc = new ToolCallAccumulator();

                        if (tcObj["id"]?.GetValue<string>() is { Length: > 0 } id) acc.Id = id;
                        var fn = tcObj["function"]?.AsObject();
                        if (fn is not null)
                        {
                            if (fn["name"]?.GetValue<string>() is { Length: > 0 } name) acc.Name = name;
                            if (fn["arguments"]?.GetValue<string>() is { } args) acc.ArgsBuffer.Append(args);
                        }
                    }
                }
            }

            if (LlmStreamFrames.Text(choice["finish_reason"]) is { Length: > 0 } finishReason)
                rawStop = finishReason;

            if (emitted.Count > 0)
                yield return new LlmStreamChunk(emitted, StopReason: null, Usage: null);
        }

        // Without a finish_reason the model never said it was done: the connection ended mid-answer, and handing the
        // pieces on as a finished answer would store a cut one.
        if (rawStop is null)
            throw new InvalidOperationException(
                $"{_providerId}: the stream ended before the model said why it stopped (no finish_reason); the answer is cut.");

        var response = ParseResponse(AssemblePlainJson(
            responseId, fingerprint, content, reasoningContent, reasoning, toolCalls, rawStop, usage));

        yield return new LlmStreamChunk(
            [.. response.Content.OfType<LlmToolUseBlock>()],
            response.StopReason,
            response.Usage)
        {
            Response = response
        };
    }

    /// <summary>The JSON a plain call returns for the answer a stream delivered in pieces.</summary>
    private static JsonObject AssemblePlainJson(
        string? responseId, string? fingerprint,
        System.Text.StringBuilder content, System.Text.StringBuilder reasoningContent, System.Text.StringBuilder reasoning,
        SortedDictionary<int, ToolCallAccumulator> toolCalls, string rawStop, JsonObject? usage)
    {
        var message = new JsonObject { ["role"] = "assistant" };
        if (content.Length > 0) message["content"] = content.ToString();
        if (reasoningContent.Length > 0) message["reasoning_content"] = reasoningContent.ToString();
        if (reasoning.Length > 0) message["reasoning"] = reasoning.ToString();

        if (toolCalls.Count > 0)
        {
            var calls = new JsonArray();
            foreach (var call in toolCalls.Values)
            {
                calls.Add(new JsonObject
                {
                    ["id"] = call.Id,
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"] = call.Name,
                        ["arguments"] = call.ArgsBuffer.Length == 0 ? "{}" : call.ArgsBuffer.ToString()
                    }
                });
            }
            message["tool_calls"] = calls;
        }

        return new JsonObject
        {
            ["id"] = responseId,
            ["system_fingerprint"] = fingerprint,
            ["choices"] = new JsonArray(new JsonObject
            {
                ["index"] = 0,
                ["message"] = message,
                ["finish_reason"] = rawStop
            }),
            ["usage"] = usage
        };
    }

    private sealed class ToolCallAccumulator
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public System.Text.StringBuilder ArgsBuffer { get; } = new();
    }

    private void ApplyAuthHeaders(HttpRequestMessage http)
    {
        var key = _factory.ApiKey;
        if (string.IsNullOrWhiteSpace(key)) return;

        http.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);

        // OpenRouter recommends a Referer/Title header for free-tier identification.
        if (_providerId == "openrouter")
        {
            http.Headers.TryAddWithoutValidation("HTTP-Referer", "https://github.com/redb-stack/redb.Route");
            http.Headers.TryAddWithoutValidation("X-Title", "redb.Route.Llm");
        }
    }

    private JsonObject BuildRequestBody(LlmRequest request, bool stream)
    {
        var messages = new JsonArray();

        if (!string.IsNullOrWhiteSpace(request.SystemPrompt))
        {
            messages.Add(new JsonObject
            {
                ["role"] = "system",
                ["content"] = request.SystemPrompt
            });
        }

        // Reasoning goes back on the assistant messages of a request that declares tools: the tool
        // loop is where a thinking model needs its earlier reasoning. A request without tools does
        // not carry the field, so a deployment that runs no tool loop never sends it.
        var includeReasoning = request.Tools.Count > 0;

        foreach (var m in request.Messages)
        {
            // Any message containing tool_result blocks must be split into one
            // role="tool" entry per result. Some engines emit them as role="user"
            // (Anthropic style) — we normalise here. Plain text/tool_use blocks
            // in the same message still emit a regular assistant/user entry.
            var hasToolResults = false;
            foreach (var b in m.Content)
            {
                if (b is LlmToolResultBlock tr)
                {
                    hasToolResults = true;
                    messages.Add(new JsonObject
                    {
                        ["role"] = "tool",
                        ["tool_call_id"] = tr.ToolUseId,
                        ["content"] = tr.OutputJson
                    });
                }
            }

            // If the message had ONLY tool results, skip the wrapping message.
            if (hasToolResults && m.Content.All(b => b is LlmToolResultBlock))
                continue;

            // Otherwise emit a normal assistant/user message (without tool_result blocks), unless it has
            // nothing to say. Content may be null only beside tool_calls, so a message with no text and
            // no tool call is refused, and one refused message in the history breaks every later call of
            // the conversation. The turn stays in the record; it is not sent.
            if (MapAssistantOrUser(m, includeReasoning) is { } mapped)
                messages.Add(mapped);
        }

        var body = new JsonObject
        {
            ["model"] = request.ModelId ?? _factory.ModelId,
            ["messages"] = messages
        };

        if (request.Temperature is { } t) body["temperature"] = t;
        if (request.MaxTokens is { } mt) body["max_tokens"] = mt;
        if (request.TopP is { } tp) body["top_p"] = tp;

        if (request.StopSequences is { Count: > 0 } stops)
        {
            var arr = new JsonArray();
            foreach (var s in stops) arr.Add(s);
            body["stop"] = arr;
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

    private static JsonObject? MapAssistantOrUser(LlmMessage m, bool includeReasoning)
    {
        var toolUses = new List<LlmToolUseBlock>();
        var text = new System.Text.StringBuilder();
        LlmThinkingBlock? thinking = null;

        foreach (var block in m.Content)
        {
            switch (block)
            {
                case LlmTextBlock tb: text.Append(tb.Text); break;
                case LlmToolUseBlock tu: toolUses.Add(tu); break;
                // The wire's only place for thinking is reasoning_content, and its payload is the text.
                case LlmThinkingBlock th when !string.IsNullOrEmpty(th.Text): thinking ??= th; break;
            }
        }

        if (text.Length == 0 && toolUses.Count == 0) return null;

        var msg = new JsonObject { ["role"] = m.Role };
        msg["content"] = text.Length > 0 ? text.ToString() : null;

        if (toolUses.Count > 0)
        {
            var calls = new JsonArray();
            foreach (var tu in toolUses)
            {
                calls.Add(new JsonObject
                {
                    ["id"] = tu.ToolUseId,
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"] = tu.Name,
                        ["arguments"] = tu.InputJson
                    }
                });
            }
            msg["tool_calls"] = calls;
        }

        if (includeReasoning && thinking is not null)
            msg["reasoning_content"] = thinking.Text;

        return msg;
    }

    /// <summary>
    /// Projects a capability into the provider's <c>tools[]</c> entry. Only the name, the description
    /// and the input schema travel: <see cref="LlmToolSafety"/> is server-side metadata and never
    /// appears on the wire.
    /// </summary>
    private static JsonObject MapTool(LlmToolCapability cap)
    {
        JsonNode parameters;
        try
        {
            parameters = JsonNode.Parse(cap.InputSchema) ?? new JsonObject { ["type"] = "object" };
        }
        catch (JsonException)
        {
            parameters = new JsonObject { ["type"] = "object" };
        }

        return new JsonObject
        {
            ["type"] = "function",
            ["function"] = new JsonObject
            {
                ["name"] = cap.Name,
                ["description"] = cap.Description,
                ["parameters"] = parameters
            }
        };
    }

    private static LlmResponse ParseResponse(JsonObject json)
    {
        var choices = json["choices"] as JsonArray;
        if (choices is null || choices.Count == 0)
            throw new InvalidOperationException("OpenAI response: no choices.");

        var first = choices[0]!.AsObject();
        var message = first["message"]?.AsObject()
                      ?? throw new InvalidOperationException("OpenAI response: no message in choice.");

        var blocks = new List<LlmContentBlock>();

        // DeepSeek's thinking models answer with the chain of thought in reasoning_content beside
        // the visible content. It is a block, not text: callers asked for the answer, and the
        // reasoning belongs to the round trip back to the provider.
        if (message["reasoning_content"] is JsonValue rc
            && rc.TryGetValue<string>(out var reasoningContent)
            && !string.IsNullOrEmpty(reasoningContent))
        {
            blocks.Add(new LlmThinkingBlock(reasoningContent));
        }

        if (message["content"] is JsonValue v && v.TryGetValue<string>(out var textContent) && !string.IsNullOrEmpty(textContent))
            blocks.Add(new LlmTextBlock(textContent));
        else if (message["reasoning"] is JsonValue rv && rv.TryGetValue<string>(out var reasoningText) && !string.IsNullOrEmpty(reasoningText))
        {
            // Reasoning models (gpt-oss-*, zai-glm-*, etc.) put their stream into
            // "reasoning" instead of "content" when max_tokens cuts the visible answer.
            // We surface it as text so callers still see something useful.
            blocks.Add(new LlmTextBlock(reasoningText));
        }

        if (message["tool_calls"] is JsonArray toolCalls)
        {
            foreach (var call in toolCalls)
            {
                if (call is not JsonObject callObj) continue;
                var fn = callObj["function"]?.AsObject();
                if (fn is null) continue;
                var id = callObj["id"]?.GetValue<string>() ?? Guid.NewGuid().ToString("N");
                var name = fn["name"]?.GetValue<string>() ?? "";
                var args = fn["arguments"]?.GetValue<string>() ?? "{}";
                blocks.Add(new LlmToolUseBlock(id, name, args));
            }
        }

        if (blocks.Count == 0) blocks.Add(new LlmTextBlock(string.Empty));

        var rawStop = first["finish_reason"]?.GetValue<string>();
        var stop = rawStop switch
        {
            "stop" => LlmStopReason.EndTurn,
            "tool_calls" => LlmStopReason.ToolUse,
            "length" => LlmStopReason.MaxTokens,
            "content_filter" => LlmStopReason.Other,
            _ => LlmStopReason.Other
        };

        var usage = LlmUsage.Empty;
        if (json["usage"] is JsonObject u)
        {
            var inT = u["prompt_tokens"]?.GetValue<int>() ?? 0;
            var outT = u["completion_tokens"]?.GetValue<int>() ?? 0;
            usage = new LlmUsage(inT, outT);
        }

        // OpenAI exposes system_fingerprint at the response root; xAI / Together
        // echo it on most models; Anthropic-compat / Gemini-compat / Ollama
        // typically leave it null. Auditors compare the value across otherwise
        // identical calls to detect silent backend re-releases.
        var fingerprint = json["system_fingerprint"]?.GetValue<string?>();
        var responseId = json["id"]?.GetValue<string?>();

        return new LlmResponse
        {
            Content = blocks,
            StopReason = stop,
            Usage = usage,
            RawStopReason = rawStop,
            ProviderSystemFingerprint = fingerprint,
            ProviderResponseId = responseId
        };
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

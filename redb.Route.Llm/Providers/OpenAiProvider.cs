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
        WriteIndented = false
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

    /// <summary>Convenience constructor that builds an internal HttpClient with the factory timeout.</summary>
    public static OpenAiProvider Create(LlmConnectionFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        var http = new HttpClient
        {
            Timeout = TimeSpan.FromMilliseconds(Math.Max(1000, factory.RequestTimeoutMs))
        };
        return new OpenAiProvider(factory, http);
    }

    /// <inheritdoc />
    public string ProviderId => _providerId;

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
        "deepseek" => new("https://api.deepseek.com/v1/"),
        "grok" or "xai" => new("https://api.x.ai/v1/"),
        "ollama" => new("http://localhost:11434/v1/"),
        "lmstudio" => new("http://localhost:1234/v1/"),
        _ => new("https://api.openai.com/v1/")
    };

    /// <inheritdoc />
    public async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var body = BuildRequestBody(request, stream: false);
        using var http = new HttpRequestMessage(HttpMethod.Post, _endpoint)
        {
            Content = JsonContent.Create(body, options: JsonOpts)
        };
        ApplyAuthHeaders(http);

        using var resp = await _http.SendAsync(http, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            var raw = await SafeReadAsync(resp, ct).ConfigureAwait(false);
            throw new HttpRequestException(
                $"{_providerId}: {(int)resp.StatusCode} {resp.ReasonPhrase} from {_endpoint}. Body: {raw}");
        }

        var json = await resp.Content.ReadFromJsonAsync<JsonObject>(JsonOpts, ct).ConfigureAwait(false)
                   ?? throw new InvalidOperationException($"{_providerId}: empty response body.");

        return ParseResponse(json);
    }

    /// <summary>
    /// True OpenAI SSE streaming: yields one chunk per <c>data:</c> frame.
    /// Text deltas surface as <see cref="LlmTextBlock"/>; tool calls are accumulated
    /// per-index and emitted as a single complete <see cref="LlmToolUseBlock"/>
    /// in the final chunk together with <c>StopReason</c> and <c>Usage</c>.
    /// </summary>
    public async IAsyncEnumerable<LlmStreamChunk> StreamAsync(
        LlmRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var body = BuildRequestBody(request, stream: true);
        body["stream_options"] = new JsonObject { ["include_usage"] = true };

        using var http = new HttpRequestMessage(HttpMethod.Post, _endpoint)
        {
            Content = JsonContent.Create(body, options: JsonOpts)
        };
        http.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        ApplyAuthHeaders(http);

        using var resp = await _http.SendAsync(http, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            var raw = await SafeReadAsync(resp, ct).ConfigureAwait(false);
            throw new HttpRequestException(
                $"{_providerId}: {(int)resp.StatusCode} {resp.ReasonPhrase} from {_endpoint}. Body: {raw}");
        }

        using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream);

        var toolCalls = new SortedDictionary<int, ToolCallAccumulator>();
        LlmStopReason? finalStop = null;
        string? rawStop = null;
        LlmUsage? finalUsage = null;

        while (!reader.EndOfStream)
        {
            ct.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null) break;
            if (line.Length == 0) continue;
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;

            var payload = line.AsSpan(5).TrimStart().ToString();
            if (payload.Length == 0) continue;
            if (payload == "[DONE]") break;

            JsonObject? frame;
            try { frame = JsonNode.Parse(payload) as JsonObject; }
            catch (JsonException) { continue; }
            if (frame is null) continue;

            // Some providers attach usage on a terminal usage-only frame (no choices).
            if (frame["usage"] is JsonObject uObj)
            {
                var inT = uObj["prompt_tokens"]?.GetValue<int>() ?? 0;
                var outT = uObj["completion_tokens"]?.GetValue<int>() ?? 0;
                finalUsage = new LlmUsage(inT, outT);
            }

            if (frame["choices"] is not JsonArray choices || choices.Count == 0) continue;
            var choice = choices[0]?.AsObject();
            if (choice is null) continue;

            var delta = choice["delta"]?.AsObject();
            var emitted = new List<LlmContentBlock>();

            if (delta is not null)
            {
                if (delta["content"] is JsonValue cv
                    && cv.TryGetValue<string>(out var deltaText)
                    && !string.IsNullOrEmpty(deltaText))
                {
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

            if (choice["finish_reason"] is JsonValue fv
                && fv.TryGetValue<string>(out var rs)
                && !string.IsNullOrEmpty(rs))
            {
                rawStop = rs;
                finalStop = rs switch
                {
                    "stop" => LlmStopReason.EndTurn,
                    "tool_calls" => LlmStopReason.ToolUse,
                    "length" => LlmStopReason.MaxTokens,
                    _ => LlmStopReason.Other
                };
            }

            if (emitted.Count > 0)
                yield return new LlmStreamChunk(emitted, StopReason: null, Usage: null);
        }

        // Terminal chunk: completed tool calls + stop reason + usage.
        var finalBlocks = new List<LlmContentBlock>();
        foreach (var kv in toolCalls)
        {
            finalBlocks.Add(new LlmToolUseBlock(
                kv.Value.Id ?? Guid.NewGuid().ToString("N"),
                kv.Value.Name ?? string.Empty,
                kv.Value.ArgsBuffer.Length == 0 ? "{}" : kv.Value.ArgsBuffer.ToString()));
        }

        yield return new LlmStreamChunk(
            finalBlocks,
            finalStop ?? LlmStopReason.EndTurn,
            finalUsage ?? LlmUsage.Empty);
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

            // Otherwise emit a normal assistant/user message (without tool_result blocks).
            messages.Add(MapAssistantOrUser(m));
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

    private static JsonObject MapAssistantOrUser(LlmMessage m)
    {
        var toolUses = new List<LlmToolUseBlock>();
        var text = new System.Text.StringBuilder();

        foreach (var block in m.Content)
        {
            switch (block)
            {
                case LlmTextBlock tb: text.Append(tb.Text); break;
                case LlmToolUseBlock tu: toolUses.Add(tu); break;
            }
        }

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

        return msg;
    }

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

        return new LlmResponse
        {
            Content = blocks,
            StopReason = stop,
            Usage = usage,
            RawStopReason = rawStop,
            ProviderSystemFingerprint = fingerprint
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

using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace redb.Route.Llm.Providers;

/// <summary>
/// Transport for any OpenAI-compatible embeddings endpoint
/// (POST <c>{baseUrl}/embeddings</c>, body <c>{"model":..,"input":[..]}</c>,
/// response <c>{"data":[{"index":i,"embedding":[..]}]}</c>).
/// <para>
/// One implementation, many providers — same <see cref="LlmConnectionFactory"/>
/// (Provider / BaseUrl / ApiKey / ModelId) as <see cref="OpenAiProvider"/>, so it
/// talks to OpenAI, Mistral, Together, DeepSeek, Gemini (compat), vLLM,
/// llama.cpp, Ollama, LM Studio, … Set <see cref="LlmConnectionFactory.ModelId"/>
/// to the embedding model (e.g. <c>text-embedding-3-small</c>), not the chat model.
/// </para>
/// </summary>
public sealed class OpenAiEmbeddingProvider : IEmbeddingProvider
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly LlmConnectionFactory _factory;
    private readonly HttpClient _http;
    private readonly Uri _endpoint;
    private readonly string _providerId;

    /// <summary>Creates the provider with an externally owned <paramref name="http"/>.</summary>
    public OpenAiEmbeddingProvider(LlmConnectionFactory factory, HttpClient http)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _http = http ?? throw new ArgumentNullException(nameof(http));

        var baseUrl = factory.BaseUrl ?? OpenAiProvider.ResolveDefaultBaseUrl(factory.Provider);
        _endpoint = new Uri(EnsureTrailingSlash(baseUrl), "embeddings");
        _providerId = string.IsNullOrWhiteSpace(factory.Provider) ? "openai" : factory.Provider!.ToLowerInvariant();
    }

    /// <summary>Convenience constructor that builds an internal HttpClient with the factory timeout.</summary>
    public static OpenAiEmbeddingProvider Create(LlmConnectionFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        var http = new HttpClient
        {
            Timeout = TimeSpan.FromMilliseconds(Math.Max(1000, factory.RequestTimeoutMs))
        };
        return new OpenAiEmbeddingProvider(factory, http);
    }

    /// <inheritdoc />
    public string ProviderId => _providerId;

    /// <inheritdoc />
    public string ModelId => _factory.ModelId;

    /// <inheritdoc />
    public async Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        if (inputs.Count == 0) return Array.Empty<float[]>();

        var inputArray = new JsonArray();
        foreach (var s in inputs)
            inputArray.Add(JsonValue.Create(s ?? string.Empty));

        var body = new JsonObject
        {
            ["model"] = _factory.ModelId,
            ["input"] = inputArray
        };

        using var http = new HttpRequestMessage(HttpMethod.Post, _endpoint)
        {
            Content = JsonContent.Create(body, options: JsonOpts)
        };
        ApplyAuthHeaders(http);

        using var resp = await _http.SendAsync(http, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            var raw = await SafeReadAsync(resp, ct).ConfigureAwait(false);

            // Typed, like the chat providers: a caller that cannot tell "wait and retry" from
            // "your key is wrong" can only degrade blindly. Retrieval degrades to keyword on
            // ANY failure, so an expired key would look exactly like a busy server.
            throw LlmHttpErrors.FromResponse(
                _providerId, resp,
                $"{_providerId} embeddings: {(int)resp.StatusCode} {resp.ReasonPhrase} from {_endpoint}. Body: {raw}",
                raw);
        }

        var json = await resp.Content.ReadFromJsonAsync<JsonObject>(JsonOpts, ct).ConfigureAwait(false)
                   ?? throw new InvalidOperationException($"{_providerId} embeddings: empty response body.");

        return ParseEmbeddings(json);
    }

    private static IReadOnlyList<float[]> ParseEmbeddings(JsonObject json)
    {
        if (json["data"] is not JsonArray data)
            throw new InvalidOperationException("embeddings response is missing the 'data' array.");

        var result = new float[data.Count][];
        for (var i = 0; i < data.Count; i++)
        {
            var item = data[i] as JsonObject
                ?? throw new InvalidOperationException("embeddings 'data' item is not an object.");
            var vecArr = item["embedding"] as JsonArray
                ?? throw new InvalidOperationException("embeddings item is missing the 'embedding' array.");

            var vec = new float[vecArr.Count];
            for (var j = 0; j < vecArr.Count; j++)
                vec[j] = vecArr[j]!.GetValue<float>();

            // Honour the provider-reported index so the order matches the inputs.
            var index = item["index"]?.GetValue<int>() ?? i;
            if (index >= 0 && index < result.Length) result[index] = vec;
            else result[i] = vec;
        }

        for (var i = 0; i < result.Length; i++)
            result[i] ??= Array.Empty<float>();

        return result;
    }

    private void ApplyAuthHeaders(HttpRequestMessage http)
    {
        var key = _factory.ApiKey;
        if (string.IsNullOrWhiteSpace(key)) return;

        http.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);

        if (_providerId == "openrouter")
        {
            http.Headers.TryAddWithoutValidation("HTTP-Referer", "https://github.com/redb-stack/redb.Route");
            http.Headers.TryAddWithoutValidation("X-Title", "redb.Route.Llm");
        }
    }

    private static async Task<string> SafeReadAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        try { return await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false); }
        catch { return "<unreadable>"; }
    }

    private static Uri EnsureTrailingSlash(Uri uri)
    {
        var s = uri.ToString();
        return s.EndsWith('/') ? uri : new Uri(s + "/");
    }
}

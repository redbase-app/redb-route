using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace redb.Route.Llm.Providers;

/// <summary>
/// Transport for any OpenAI-compatible speech endpoint
/// (POST <c>{baseUrl}/audio/transcriptions</c>, <c>multipart/form-data</c> with a
/// <c>file</c> and a <c>model</c>, response <c>{"text": ".."}</c>).
/// <para>
/// One implementation, many providers — the same <see cref="LlmConnectionFactory"/>
/// (Provider / BaseUrl / ApiKey / ModelId) as <see cref="OpenAiProvider"/> and
/// <see cref="OpenAiEmbeddingProvider"/>, so it talks to OpenAI and Groq as readily
/// as to whisper.cpp's server, Speaches / faster-whisper-server or LM Studio on
/// <c>127.0.0.1</c>. Local and paid recognition differ by a base URL, which is the
/// point: a product can develop against its own GPU and move to a paid endpoint
/// without a line of route code changing. Set
/// <see cref="LlmConnectionFactory.ModelId"/> to the speech model
/// (e.g. <c>whisper-1</c>, <c>large-v3</c>), not the chat model.
/// </para>
/// </summary>
public sealed class OpenAiTranscriptionProvider : ITranscriptionProvider
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private readonly LlmConnectionFactory _factory;
    private readonly HttpClient _http;
    private readonly Uri _endpoint;
    private readonly string _providerId;

    /// <summary>Creates the provider with an externally owned <paramref name="http"/>.</summary>
    public OpenAiTranscriptionProvider(LlmConnectionFactory factory, HttpClient http)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _http = http ?? throw new ArgumentNullException(nameof(http));

        var baseUrl = factory.BaseUrl ?? OpenAiProvider.ResolveDefaultBaseUrl(factory.Provider);
        _endpoint = new Uri(EnsureTrailingSlash(baseUrl), "audio/transcriptions");
        _providerId = string.IsNullOrWhiteSpace(factory.Provider) ? "openai" : factory.Provider!.ToLowerInvariant();
    }

    /// <summary>Convenience constructor that builds an internal HttpClient with the factory timeout.</summary>
    public static OpenAiTranscriptionProvider Create(LlmConnectionFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        // Recognition is slower than a chat completion on the same hardware — a minute of
        // speech is minutes of compute on a modest local model — so the factory timeout is
        // the ceiling that matters here, not a formality, and those minutes are silent on the
        // wire: the package's default client keeps the connection alive with HTTP/2 pings.
        return new OpenAiTranscriptionProvider(factory, LlmHttpTransport.BuildClient(factory));
    }

    /// <inheritdoc />
    public string ProviderId => _providerId;

    /// <summary>The client this provider sends through (tests read its transport defaults).</summary>
    internal HttpClient Http => _http;

    /// <inheritdoc />
    public string ModelId => _factory.ModelId;

    /// <inheritdoc />
    public Task<TranscriptionResult> TranscribeAsync(
        TranscriptionRequest request, CancellationToken ct = default)
    {
        if (request.Audio.IsEmpty)
            throw new ArgumentException(
                "Transcription: the audio is empty. A zero-byte upload is a bug upstream, not silence — " +
                "silence is a recording that transcribes to an empty string.",
                nameof(request));

        // Limited as a whole by RequestTimeoutMs: a recording is minutes of compute before the answer.
        return LlmHttpTransport.WithinCallLimitAsync(_factory, _providerId, t => TranscribeCoreAsync(request, t), ct);
    }

    private async Task<TranscriptionResult> TranscribeCoreAsync(TranscriptionRequest request, CancellationToken ct)
    {
        var fileName = string.IsNullOrWhiteSpace(request.FileName) ? "audio.ogg" : request.FileName;

        using var form = new MultipartFormDataContent();

        var audio = new ByteArrayContent(request.Audio.ToArray());
        audio.Headers.ContentType = new MediaTypeHeaderValue(GuessMediaType(fileName));
        form.Add(audio, "file", fileName);

        form.Add(new StringContent(_factory.ModelId), "model");

        // "json" and not "verbose_json": the verbose shape carries duration and segments, but it
        // is the format local servers are likeliest not to implement, and a 400 from asking for
        // extras would cost the transcription itself. Duration is better taken from the transport
        // that delivered the recording anyway — it knows it before the upload.
        form.Add(new StringContent("json"), "response_format");

        if (!string.IsNullOrWhiteSpace(request.Language))
            form.Add(new StringContent(request.Language!), "language");

        if (!string.IsNullOrWhiteSpace(request.Prompt))
            form.Add(new StringContent(request.Prompt!), "prompt");

        using var http = LlmHttpTransport.NewRequest(_http, HttpMethod.Post, _endpoint);
        http.Content = form;
        ApplyAuthHeaders(http);

        using var resp = await _http.SendAsync(http, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);

        if (!resp.IsSuccessStatusCode)
        {
            var raw = await SafeReadAsync(resp, ct).ConfigureAwait(false);

            // Typed, like the chat and embedding providers: a route that degrades a failed
            // transcription into "please repeat, I did not catch that" must not say it to a
            // person whose recording was fine and whose server was merely busy.
            throw LlmHttpErrors.FromResponse(
                _providerId, resp,
                $"{_providerId} transcription: {(int)resp.StatusCode} {resp.ReasonPhrase} from {_endpoint}. Body: {raw}",
                raw);
        }

        return await ParseAsync(resp, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads the answer. JSON is the contract, but plain text is what several local
    /// servers return when they ignore <c>response_format</c> — and a transcription that
    /// arrived is not worth discarding over its envelope.
    /// </summary>
    private static async Task<TranscriptionResult> ParseAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        var isJson = resp.Content.Headers.ContentType?.MediaType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true;

        if (!isJson)
        {
            var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return new TranscriptionResult(text.Trim());
        }

        var json = await resp.Content.ReadFromJsonAsync<JsonObject>(JsonOpts, ct).ConfigureAwait(false)
                   ?? throw new InvalidOperationException("transcription: empty response body.");

        var recognised = json["text"]?.GetValue<string>()
            ?? throw new InvalidOperationException("transcription response is missing the 'text' field.");

        return new TranscriptionResult(
            recognised.Trim(),
            Language: json["language"]?.GetValue<string>(),
            DurationSeconds: ReadDouble(json["duration"]));
    }

    private static double? ReadDouble(JsonNode? node)
    {
        if (node is null) return null;
        try { return node.GetValue<double>(); }
        catch (FormatException) { return null; }
        catch (InvalidOperationException) { return null; }
    }

    /// <summary>
    /// Content type for the upload, by extension. Speech endpoints choose a demuxer from
    /// what they are handed, so a Telegram voice note (<c>.oga</c>, Ogg/Opus) announced as
    /// generic bytes is a decode failure on servers that would otherwise read it fine.
    /// </summary>
    private static string GuessMediaType(string fileName) =>
        Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".oga" or ".ogg" or ".opus" => "audio/ogg",
            ".mp3" => "audio/mpeg",
            ".m4a" => "audio/mp4",
            ".wav" => "audio/wav",
            ".flac" => "audio/flac",
            ".webm" => "audio/webm",
            ".mp4" => "video/mp4",
            ".mpeg" or ".mpga" => "audio/mpeg",
            _ => "application/octet-stream"
        };

    private void ApplyAuthHeaders(HttpRequestMessage http)
    {
        var key = _factory.ApiKey;
        if (string.IsNullOrWhiteSpace(key)) return;

        http.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
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

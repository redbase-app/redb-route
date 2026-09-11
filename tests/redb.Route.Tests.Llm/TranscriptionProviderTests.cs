using System.Net;
using System.Text;

using FluentAssertions;

using redb.Route.Llm;
using redb.Route.Llm.Providers;

namespace redb.Route.Tests.Llm;

/// <summary>
/// <see cref="OpenAiTranscriptionProvider"/> request/response mapping, driven through a
/// stub <see cref="HttpMessageHandler"/> — no live key, no audio, deterministic.
/// <para>
/// The interesting part of a speech transport is not the happy path but the shape of the
/// upload: speech endpoints choose a decoder from the file name they are handed, and a
/// route whose Ogg/Opus voice note arrives announced as "some bytes" gets a decode error
/// instead of a transcription.
/// </para>
/// </summary>
public sealed class TranscriptionProviderTests
{
    private sealed class StubHandler(
        string responseBody,
        HttpStatusCode status = HttpStatusCode.OK,
        string mediaType = "application/json") : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest = request;
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, mediaType)
            };
        }
    }

    private static LlmConnectionFactory Factory(string provider = "openai", string model = "whisper-1") =>
        new() { Provider = provider, ModelId = model, ApiKey = "sk-test" };

    private static TranscriptionRequest Voice(string fileName = "voice.oga") =>
        new(new byte[] { 0x4F, 0x67, 0x67, 0x53 }, fileName, Language: "ru");

    [Fact]
    public async Task TranscribeAsync_ReadsTheText_AndShapesTheUpload()
    {
        var handler = new StubHandler("""{"text":"  привет  "}""");
        var provider = new OpenAiTranscriptionProvider(Factory(), new HttpClient(handler));

        var result = await provider.TranscribeAsync(Voice());

        result.Text.Should().Be("привет", "trailing whitespace is the model's, not the speaker's");

        handler.LastRequest!.RequestUri!.ToString().Should().Be("https://api.openai.com/v1/audio/transcriptions");
        handler.LastRequest.Headers.Authorization!.ToString().Should().Be("Bearer sk-test");

        var body = handler.LastBody!;
        body.Should().Contain("whisper-1", "the model has to be named in the form, not the URL");
        body.Should().Contain("filename=voice.oga",
            "speech endpoints pick a demuxer by extension — the name is what decodes the file");
        body.Should().Contain("audio/ogg");
        body.Should().Contain("name=language").And.Contain("ru");
    }

    [Fact]
    public async Task TranscribeAsync_SilenceIsAnEmptyString_NotAFailure()
    {
        var handler = new StubHandler("""{"text":""}""");
        var provider = new OpenAiTranscriptionProvider(Factory(), new HttpClient(handler));

        var result = await provider.TranscribeAsync(Voice());

        // Silence and music transcribe to nothing. That is an answer the route can act on
        // ("I did not hear speech"), and turning it into an exception would make it
        // indistinguishable from a broken server.
        result.Text.Should().BeEmpty();
    }

    [Fact]
    public async Task TranscribeAsync_AcceptsAPlainTextAnswer()
    {
        // Several local servers ignore response_format and answer with the bare text. A
        // transcription that arrived is not worth discarding over its envelope.
        var handler = new StubHandler("привет", mediaType: "text/plain");
        var provider = new OpenAiTranscriptionProvider(Factory("lmstudio"), new HttpClient(handler));

        (await provider.TranscribeAsync(Voice())).Text.Should().Be("привет");
    }

    [Fact]
    public async Task TranscribeAsync_ReadsLanguageAndDurationWhenTheProviderReportsThem()
    {
        var handler = new StubHandler("""{"text":"hi","language":"russian","duration":12.5}""");
        var provider = new OpenAiTranscriptionProvider(Factory(), new HttpClient(handler));

        var result = await provider.TranscribeAsync(Voice());

        result.Language.Should().Be("russian");
        result.DurationSeconds.Should().Be(12.5);
    }

    [Fact]
    public async Task TranscribeAsync_TypesTheFailure_SoDegradingIsNotBlind()
    {
        var handler = new StubHandler("""{"error":"slow down"}""", HttpStatusCode.TooManyRequests);
        var provider = new OpenAiTranscriptionProvider(Factory(), new HttpClient(handler));

        // A route that answers "sorry, I did not catch that" must not say it to a person
        // whose recording was fine and whose server was merely busy.
        var act = async () => await provider.TranscribeAsync(Voice());
        await act.Should().ThrowAsync<LlmRateLimitException>();
    }

    [Fact]
    public async Task TranscribeAsync_EmptyAudioIsRejectedBeforeTheCall()
    {
        var handler = new StubHandler("""{"text":"should never be asked for"}""");
        var provider = new OpenAiTranscriptionProvider(Factory(), new HttpClient(handler));

        var act = async () => await provider.TranscribeAsync(
            new TranscriptionRequest(ReadOnlyMemory<byte>.Empty, "voice.oga"));

        await act.Should().ThrowAsync<ArgumentException>();
        handler.LastRequest.Should().BeNull("a zero-byte upload is a bug upstream, not silence");
    }

    [Fact]
    public async Task TranscribeAsync_LocalServerNeedsNoKey()
    {
        var handler = new StubHandler("""{"text":"локально"}""");
        var factory = new LlmConnectionFactory
        {
            Provider = "local",
            ModelId = "large-v3",
            BaseUrl = new Uri("http://127.0.0.1:8083/v1/")
        };
        var provider = new OpenAiTranscriptionProvider(factory, new HttpClient(handler));

        (await provider.TranscribeAsync(Voice())).Text.Should().Be("локально");

        handler.LastRequest!.RequestUri!.ToString()
            .Should().Be("http://127.0.0.1:8083/v1/audio/transcriptions");
        handler.LastRequest.Headers.Authorization.Should().BeNull(
            "a whisper server on loopback has no key to send, and sending an empty Bearer is a 401");
        provider.ProviderId.Should().Be("local");
    }
}

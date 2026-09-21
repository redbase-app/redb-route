using System.Net;
using System.Text;

namespace redb.Route.Tests.Llm;

/// <summary>
/// Every model client of the package keeps a long silent call alive the same way: HTTP/2 when the server offers it,
/// and a PING frame while the request is in flight. The client built by <c>Create</c> asks for HTTP/2, and every
/// request carries the client's version: a hand-built request starts at HTTP/1.1, and then the pings never happen.
/// </summary>
public sealed class LlmHttpTransportTests
{
    /// <summary>Records the HTTP version every request asked for and answers with a fixed body.</summary>
    private sealed class VersionRecordingHandler(string body, string mediaType = "application/json") : HttpMessageHandler
    {
        public List<(Version Version, HttpVersionPolicy Policy)> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add((request.Version, request.VersionPolicy));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, mediaType)
            });
        }
    }

    /// <summary>A caller's client that asks for HTTP/2, the way the package's own default client does.</summary>
    private static HttpClient Http2Client(HttpMessageHandler handler) => new(handler)
    {
        DefaultRequestVersion = HttpVersion.Version20,
        DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower
    };

    private static LlmConnectionFactory Factory(string provider, string model) =>
        new() { Provider = provider, ModelId = model, ApiKey = "sk-test", RequestTimeoutMs = 90_000 };

    [Fact]
    public void OpenAi_DefaultClient_AsksForHttp2()
    {
        var provider = OpenAiProvider.Create(Factory("deepseek", "deepseek-flash"));

        provider.Http.DefaultRequestVersion.Should().Be(HttpVersion.Version20,
            "the keep-alive pings ride on HTTP/2 frames; a thinking model's call is minutes of silence");
        provider.Http.DefaultVersionPolicy.Should().Be(HttpVersionPolicy.RequestVersionOrLower, "HTTP/1.1 stays the fallback");
        provider.Http.Timeout.Should().Be(Timeout.InfiniteTimeSpan,
            "RequestTimeoutMs limits the whole call and is applied per call (RequestTimeoutTests)");
    }

    [Fact]
    public async Task OpenAi_Request_CarriesTheClientsHttpVersion()
    {
        var handler = new VersionRecordingHandler("""{"choices":[{"message":{"role":"assistant","content":"done"}}]}""");
        var provider = new OpenAiProvider(Factory("deepseek", "deepseek-flash"), Http2Client(handler));

        await provider.CompleteAsync(new LlmRequest { Messages = [LlmMessage.User("hi")] });

        var sent = handler.Requests.Should().ContainSingle().Subject;
        sent.Version.Should().Be(HttpVersion.Version20, "a hand-built request must carry the client's version");
        sent.Policy.Should().Be(HttpVersionPolicy.RequestVersionOrLower);
    }

    [Fact]
    public async Task OpenAi_StreamRequest_CarriesTheClientsHttpVersion()
    {
        var handler = new VersionRecordingHandler(
            "data: {\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n", "text/event-stream");
        var provider = new OpenAiProvider(Factory("deepseek", "deepseek-flash"), Http2Client(handler));

        await foreach (var _ in provider.StreamAsync(new LlmRequest { Messages = [LlmMessage.User("hi")] }))
        {
        }

        var sent = handler.Requests.Should().ContainSingle().Subject;
        sent.Version.Should().Be(HttpVersion.Version20, "the streaming request is hand-built too");
        sent.Policy.Should().Be(HttpVersionPolicy.RequestVersionOrLower);
    }

    [Fact]
    public void Transcription_DefaultClient_AsksForHttp2()
    {
        var provider = OpenAiTranscriptionProvider.Create(Factory("openai", "whisper-1"));

        provider.Http.DefaultRequestVersion.Should().Be(HttpVersion.Version20,
            "a long recording is minutes of compute with nothing on the wire");
        provider.Http.DefaultVersionPolicy.Should().Be(HttpVersionPolicy.RequestVersionOrLower, "HTTP/1.1 stays the fallback");
        provider.Http.Timeout.Should().Be(Timeout.InfiniteTimeSpan,
            "RequestTimeoutMs limits the whole call and is applied per call (RequestTimeoutTests)");
    }

    [Fact]
    public async Task Transcription_Request_CarriesTheClientsHttpVersion()
    {
        var handler = new VersionRecordingHandler("""{"text":"hi"}""");
        var provider = new OpenAiTranscriptionProvider(Factory("openai", "whisper-1"), Http2Client(handler));

        await provider.TranscribeAsync(new TranscriptionRequest(new byte[] { 0x4F, 0x67, 0x67, 0x53 }, "voice.oga", Language: "ru"));

        var sent = handler.Requests.Should().ContainSingle().Subject;
        sent.Version.Should().Be(HttpVersion.Version20, "a hand-built request must carry the client's version");
        sent.Policy.Should().Be(HttpVersionPolicy.RequestVersionOrLower);
    }
}

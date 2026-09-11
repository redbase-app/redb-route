using redb.Route.Telegram;

namespace redb.Route.Tests.Telegram;

/// <summary>
/// The shared HTTP transport of a bot. A getUpdates long poll is ~50 s of silence on the wire, and
/// tunnels/NATs cut a silent TLS connection at about that mark — every idle poll then dies with
/// "The response ended prematurely". HTTP/2 PING frames while the request is in flight are what
/// keeps the poll (and a long download) alive, so the transport must ask for HTTP/2 and ping well
/// inside the ~50 s window.
/// </summary>
public sealed class TelegramTransportTests
{
    [Fact]
    public void SharedHttpClient_UsesHttp2_WithKeepAlivePings()
    {
        var handler = TelegramComponent.BuildHandler();

        handler.KeepAlivePingPolicy.Should().Be(HttpKeepAlivePingPolicy.WithActiveRequests);
        handler.KeepAlivePingDelay.Should().BeGreaterThan(TimeSpan.Zero).And.BeLessThan(TimeSpan.FromSeconds(30));
        handler.KeepAlivePingTimeout.Should().BeGreaterThan(TimeSpan.Zero);

        using var http = TelegramComponent.BuildHttpClient();

        http.DefaultRequestVersion.Should().Be(System.Net.HttpVersion.Version20);
        http.DefaultVersionPolicy.Should().Be(HttpVersionPolicy.RequestVersionOrLower, "HTTP/1.1 must stay the fallback");
        http.Timeout.Should().BeGreaterThan(TimeSpan.FromSeconds(50), "must exceed Telegram's long-poll cap plus a margin");
    }

    [Fact]
    public async Task EveryRequest_IsUpgradedToHttp2_BeforeItReachesTheSocket()
    {
        // Telegram.Bot builds its own HttpRequestMessages, which start at HTTP/1.1 no matter what
        // the client's DefaultRequestVersion says — so the upgrade has to happen per request.
        var inner = new RecordingHandler();
        using var http = new HttpClient(new Http2UpgradeHandler(inner));

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.telegram.org/bot1/getMe");
        request.Version.Should().Be(System.Net.HttpVersion.Version11, "that is what a hand-built message starts with");

        using var _ = await http.SendAsync(request);

        inner.SeenVersion.Should().Be(System.Net.HttpVersion.Version20);
        inner.SeenPolicy.Should().Be(HttpVersionPolicy.RequestVersionOrLower, "HTTP/1.1 must stay the fallback");
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public Version? SeenVersion { get; private set; }
        public HttpVersionPolicy? SeenPolicy { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            SeenVersion = request.Version;
            SeenPolicy = request.VersionPolicy;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("{}") });
        }
    }
}

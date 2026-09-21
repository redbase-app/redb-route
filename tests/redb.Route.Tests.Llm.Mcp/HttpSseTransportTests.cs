using System.Net;
using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;

namespace redb.Route.Tests.Llm.Mcp;

/// <summary>
/// The MCP HTTP+SSE transport keeps its event stream alive the way the model clients do: its own client asks for
/// HTTP/2, so keep-alive pings run while the stream is open and silent between events, and the hand-built SSE
/// request carries the client's version (a hand-built request starts at HTTP/1.1, and then no pings are sent).
/// </summary>
public sealed class HttpSseTransportTests
{
    private static McpTransport Sse() => new()
    {
        Kind = McpTransportKind.HttpSse,
        BaseUrl = "https://mcp.example.test/mcp"
    };

    /// <summary>Records the first request and answers 404, so the SSE pump ends at once.</summary>
    private sealed class FirstRequestHandler : HttpMessageHandler
    {
        public TaskCompletionSource<(HttpMethod Method, Version Version, HttpVersionPolicy Policy)> First { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            First.TrySetResult((request.Method, request.Version, request.VersionPolicy));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    [Fact]
    public async Task OwnClient_AsksForHttp2()
    {
        await using var client = new HttpSseMcpClient("probe", Sse(), NullLogger.Instance);

        client.Http.DefaultRequestVersion.Should().Be(HttpVersion.Version20,
            "the keep-alive pings ride on HTTP/2 frames; an event stream can be silent for minutes");
        client.Http.DefaultVersionPolicy.Should().Be(HttpVersionPolicy.RequestVersionOrLower, "HTTP/1.1 stays the fallback");
    }

    [Fact]
    public async Task SseRequest_CarriesTheClientsHttpVersion()
    {
        var handler = new FirstRequestHandler();
        var http = new HttpClient(handler)
        {
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower
        };
        await using var client = new HttpSseMcpClient("probe", Sse(), NullLogger.Instance, http);

        // The SSE channel opens when the transport starts; InitializeAsync would run the MCP handshake on top of it.
        var start = typeof(HttpSseMcpClient).GetMethod("StartTransportAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await (Task)start.Invoke(client, [CancellationToken.None])!;

        var sent = await handler.First.Task.WaitAsync(TimeSpan.FromSeconds(10));
        sent.Method.Should().Be(HttpMethod.Get, "the first request of the transport is the SSE channel");
        sent.Version.Should().Be(HttpVersion.Version20, "the hand-built SSE request must carry the client's version");
        sent.Policy.Should().Be(HttpVersionPolicy.RequestVersionOrLower);
    }
}

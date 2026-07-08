using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Http;

namespace redb.Route.Tests.Http;

/// <summary>
/// Wire-level tests for <see cref="HttpConsumer"/> when the route puts an
/// <see cref="IAsyncEnumerable{T}"/> of <see cref="string"/> into
/// <see cref="IExchange.Out"/>.<see cref="IMessage.Body"/>.
/// <para>
/// Covers the two encodings the consumer picks based on <c>Content-Type</c>:
///   <list type="bullet">
///     <item><description><c>text/event-stream</c> → SSE framing
///       (<c>data: …</c> per chunk plus a terminal <c>event: done</c> with the
///       JSON trailer of late-bound headers).</description></item>
///     <item><description>anything else → chunked plain text, one flush per
///       yield.</description></item>
///   </list>
/// Also covers the streaming-headers contract
/// (<c>Cache-Control: no-cache, no-transform</c>, <c>X-Accel-Buffering: no</c>)
/// and cancellation propagation when the client closes the response stream
/// mid-flight (server-side enumerator observes <see cref="OperationCanceledException"/>).
/// </para>
/// </summary>
[Collection("HttpServer")]
public class HttpStreamingTests : IAsyncLifetime
{
    private SharedHttpServerManager _serverManager = null!;
    private HttpClient _client = null!;
    private int _port;
    private HttpConsumer? _consumer;

    public Task InitializeAsync()
    {
        _port = GetFreePort();
        _serverManager = new SharedHttpServerManager();
        // PooledConnectionLifetime tiny so a disposed response gives the server
        // an immediate Kestrel-side RequestAborted (cancellation test).
        _client = new HttpClient(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromSeconds(1)
        });
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        if (_consumer is not null) await _consumer.Stop();
        await _serverManager.DisposeAsync();
    }

    /// <summary>
    /// Builds a consumer wired to the shared server. The processor runs
    /// <paramref name="onProcess"/> which is expected to populate
    /// <see cref="IExchange.Out"/> with a streaming body.
    /// </summary>
    private HttpConsumer CreateStreamingConsumer(string path, Func<IExchange, Task> onProcess)
    {
        var component = new HttpComponent { ServerManager = _serverManager };
        var parameters = new Dictionary<string, string>
        {
            ["host"] = "127.0.0.1",
            ["port"] = _port.ToString(),
            ["inOut"] = "true"
        };
        var uriPath = $"/127.0.0.1:{_port}{path}";
        var uri = new EndpointUri("http", uriPath, $"http:{uriPath}", parameters);
        var endpoint = (HttpEndpoint)component.CreateEndpoint(uri);

        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(ci => onProcess(ci.Arg<IExchange>()));

        _consumer = new HttpConsumer(endpoint, processor, endpoint.EndpointOptions, _serverManager);
        return _consumer;
    }

    /// <summary>
    /// Iterator helper. Yields <paramref name="chunks"/> with a small delay
    /// between them so the test can verify per-chunk flushing on the wire.
    /// When iteration completes (or is cancelled) writes late-bound headers
    /// onto <paramref name="responseMsg"/> so the SSE <c>done</c> trailer can
    /// pick them up — matches the contract <c>LlmProducer</c> follows.
    /// </summary>
    private static async IAsyncEnumerable<string> ScriptedChunksAsync(
        string[] chunks,
        IMessage responseMsg,
        TaskCompletionSource? observedCancellation = null,
        int delayMs = 30,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        try
        {
            foreach (var c in chunks)
            {
                await Task.Delay(delayMs, ct).ConfigureAwait(false);
                yield return c;
            }
            // Late-bound summary headers — read by the SSE trailer builder.
            responseMsg.Headers["llm.tokens.in"] = 7;
            responseMsg.Headers["llm.tokens.out"] = chunks.Length;
            responseMsg.Headers["llm.stop_reason"] = "EndTurn";
        }
        finally
        {
            if (ct.IsCancellationRequested)
                observedCancellation?.TrySetResult();
        }
    }

    [Fact]
    public async Task Sse_PerChunkFlush_AndDoneTrailer()
    {
        var consumer = CreateStreamingConsumer("/sse", ex =>
        {
            ex.Out = ex.In.Clone();
            ex.Out.ContentType = "text/event-stream";
            ex.Out.Body = ScriptedChunksAsync(["alpha", "beta", "gamma"], ex.Out);
            return Task.CompletedTask;
        });
        await consumer.Start();

        using var resp = await _client.GetAsync(
            $"http://127.0.0.1:{_port}/sse",
            HttpCompletionOption.ResponseHeadersRead);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        resp.Content.Headers.ContentType!.MediaType.Should().Be("text/event-stream");

        // Streaming-headers contract.
        resp.Headers.TryGetValues("Cache-Control", out var cc).Should().BeTrue();
        string.Join(',', cc!).Should().Contain("no-cache").And.Contain("no-transform");
        resp.Headers.TryGetValues("X-Accel-Buffering", out var xab).Should().BeTrue();
        string.Join(',', xab!).Should().Be("no");

        // Chunked, no Content-Length up front.
        resp.Content.Headers.ContentLength.Should().BeNull();

        var body = await resp.Content.ReadAsStringAsync();

        body.Should().Contain("data: alpha\n");
        body.Should().Contain("data: beta\n");
        body.Should().Contain("data: gamma\n");
        body.Should().Contain("event: done\n");

        // Trailer JSON carries late-bound headers.
        var trailerIdx = body.IndexOf("event: done", StringComparison.Ordinal);
        var trailer = body[trailerIdx..];
        trailer.Should().Contain("\"llm.tokens.in\"");
        trailer.Should().Contain("\"llm.tokens.out\"");
        trailer.Should().Contain("\"llm.stop_reason\"");

        // Confirm the JSON payload after 'data: ' on the done event parses.
        var dataPrefix = "data: ";
        var dataLine = trailer
            .Split('\n')
            .First(l => l.StartsWith(dataPrefix, StringComparison.Ordinal));
        using var json = JsonDocument.Parse(dataLine[dataPrefix.Length..]);
        json.RootElement.GetProperty("llm.tokens.out").GetString().Should().Be("3");
        json.RootElement.GetProperty("llm.stop_reason").GetString().Should().Be("EndTurn");
    }

    [Fact]
    public async Task ChunkedPlain_NoSseFraming_NoTrailer()
    {
        var consumer = CreateStreamingConsumer("/chunked", ex =>
        {
            ex.Out = ex.In.Clone();
            ex.Out.ContentType = "text/plain";
            ex.Out.Body = ScriptedChunksAsync(["foo-", "bar-", "baz"], ex.Out);
            return Task.CompletedTask;
        });
        await consumer.Start();

        using var resp = await _client.GetAsync(
            $"http://127.0.0.1:{_port}/chunked",
            HttpCompletionOption.ResponseHeadersRead);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        resp.Content.Headers.ContentType!.MediaType.Should().Be("text/plain");
        resp.Content.Headers.ContentLength.Should().BeNull(); // chunked

        var body = await resp.Content.ReadAsStringAsync();

        body.Should().Be("foo-bar-baz");           // pure concatenation
        body.Should().NotContain("data: ");        // no SSE framing
        body.Should().NotContain("event: done");
    }

    [Fact]
    public async Task ChunksArriveProgressively_NotBuffered()
    {
        // 6 chunks with 80 ms spacing → no way the whole body could be buffered
        // and delivered as one read. We assert that the first chunk lands well
        // before the last yield happens.
        var consumer = CreateStreamingConsumer("/progress", ex =>
        {
            ex.Out = ex.In.Clone();
            ex.Out.ContentType = "text/plain";
            ex.Out.Body = ScriptedChunksAsync(
                ["c0", "c1", "c2", "c3", "c4", "c5"], ex.Out, delayMs: 80);
            return Task.CompletedTask;
        });
        await consumer.Start();

        using var resp = await _client.GetAsync(
            $"http://127.0.0.1:{_port}/progress",
            HttpCompletionOption.ResponseHeadersRead);

        var stream = await resp.Content.ReadAsStreamAsync();
        var buffer = new byte[256];

        var start = DateTime.UtcNow;
        var firstReadAt = TimeSpan.Zero;
        var total = new StringBuilder();
        while (true)
        {
            var n = await stream.ReadAsync(buffer);
            if (n == 0) break;
            if (firstReadAt == TimeSpan.Zero) firstReadAt = DateTime.UtcNow - start;
            total.Append(Encoding.UTF8.GetString(buffer, 0, n));
        }
        var totalElapsed = DateTime.UtcNow - start;

        total.ToString().Should().Be("c0c1c2c3c4c5");

        // The full enumeration takes ~480 ms (6 × 80 ms). The first chunk must
        // arrive a healthy margin before the whole thing finishes — proving
        // the consumer is not buffering. Allow generous CI slack.
        firstReadAt.Should().BeLessThan(totalElapsed - TimeSpan.FromMilliseconds(150),
            "first byte should arrive well before the last yield");
    }

    [Fact]
    public async Task ClientCancel_PropagatesToEnumerator()
    {
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var consumer = CreateStreamingConsumer("/cancel", ex =>
        {
            ex.Out = ex.In.Clone();
            ex.Out.ContentType = "text/event-stream";
            // 20 chunks, 100 ms apart — gives the test plenty of time to abort.
            ex.Out.Body = ScriptedChunksAsync(
                Enumerable.Range(0, 20).Select(i => $"c{i}").ToArray(),
                ex.Out,
                observedCancellation: cancelled,
                delayMs: 100);
            return Task.CompletedTask;
        });
        await consumer.Start();

        using var abort = new CancellationTokenSource();
        using (var resp = await _client.GetAsync(
            $"http://127.0.0.1:{_port}/cancel",
            HttpCompletionOption.ResponseHeadersRead,
            abort.Token))
        {
            var stream = await resp.Content.ReadAsStreamAsync();
            var buffer = new byte[64];
            // Read at least one chunk so the server is actively yielding…
            (await stream.ReadAsync(buffer)).Should().BeGreaterThan(0);
            // …then bail. Cancelling the token aborts the request on the wire
            // and Kestrel raises RequestAborted on the consumer.
            abort.Cancel();
        }

        // Server-side enumerator must observe cancellation within a reasonable
        // window (well under the full 2 s of total scripted yields).
        var observed = await Task.WhenAny(cancelled.Task, Task.Delay(2_500));
        observed.Should().BeSameAs(cancelled.Task,
            "server-side enumerator should observe client cancellation");
    }

    private static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}

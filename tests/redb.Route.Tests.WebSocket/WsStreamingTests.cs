using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.WebSocket;

namespace redb.Route.Tests.WebSocket;

/// <summary>
/// Wire-level tests for <see cref="WsConsumer"/> when the route puts an
/// <see cref="IAsyncEnumerable{T}"/> of <see cref="string"/> into
/// <see cref="IExchange.Out"/>.<see cref="IMessage.Body"/>: every yield must
/// turn into one text frame with <c>endOfMessage=true</c>, in order, all
/// belonging to the same client request.
/// </summary>
public class WsStreamingTests : IAsyncLifetime
{
    private int _port;
    private WsConsumer? _consumer;

    public Task InitializeAsync()
    {
        _port = GetFreePort();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_consumer is not null) await _consumer.Stop();
    }

    private async Task<WsConsumer> StartStreamingConsumerAsync(Func<IExchange, Task> onProcess)
    {
        var component = new WsComponent();
        var parameters = new Dictionary<string, string>
        {
            ["messageType"] = WsMessageType.Text.ToString(),
            ["inOut"] = "true"
        };
        var uri = new EndpointUri("ws", $"/127.0.0.1:{_port}/ws",
            $"ws:127.0.0.1:{_port}/ws", parameters);
        var endpoint = (WsEndpoint)component.CreateEndpoint(uri);

        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(ci => onProcess(ci.Arg<IExchange>()));

        _consumer = new WsConsumer(endpoint, processor, endpoint.EndpointOptions);
        await _consumer.Start();
        return _consumer;
    }

    /// <summary>Reads one full WebSocket text message (drains until EOM).</summary>
    private static async Task<string> ReceiveMessageAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[1024];
        var sb = new StringBuilder();
        while (true)
        {
            var result = await ws.ReceiveAsync(buffer, ct).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close) return string.Empty;
            sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
            if (result.EndOfMessage) return sb.ToString();
        }
    }

    [Fact]
    public async Task Streaming_OneFramePerYield_OrderPreserved()
    {
        await StartStreamingConsumerAsync(ex =>
        {
            ex.Out = ex.In.Clone();
            ex.Out.Body = ChunksAsync(["alpha", "beta", "gamma"], delayMs: 10);
            return Task.CompletedTask;
        });

        using var client = new ClientWebSocket();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await client.ConnectAsync(new Uri($"ws://127.0.0.1:{_port}/ws"), cts.Token);

        await client.SendAsync(Encoding.UTF8.GetBytes("go"), WebSocketMessageType.Text,
            endOfMessage: true, cts.Token);

        (await ReceiveMessageAsync(client, cts.Token)).Should().Be("alpha");
        (await ReceiveMessageAsync(client, cts.Token)).Should().Be("beta");
        (await ReceiveMessageAsync(client, cts.Token)).Should().Be("gamma");

        // No CloseAsync: WsConsumer's receive loop does not echo a Close frame
        // (it just returns), so a client-initiated close would raise
        // "remote party closed without handshake". Tracked separately.
    }

    [Fact]
    public async Task Streaming_EmptyChunksSkipped()
    {
        await StartStreamingConsumerAsync(ex =>
        {
            ex.Out = ex.In.Clone();
            // Empty / null strings must NOT produce wire frames — matches the
            // contract WsConsumer follows (and what LLM providers emit between
            // SSE keep-alives).
            ex.Out.Body = ChunksAsync(["one", "", "two", "", "three"], delayMs: 10);
            return Task.CompletedTask;
        });

        using var client = new ClientWebSocket();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await client.ConnectAsync(new Uri($"ws://127.0.0.1:{_port}/ws"), cts.Token);
        await client.SendAsync(Encoding.UTF8.GetBytes("go"), WebSocketMessageType.Text,
            endOfMessage: true, cts.Token);

        (await ReceiveMessageAsync(client, cts.Token)).Should().Be("one");
        (await ReceiveMessageAsync(client, cts.Token)).Should().Be("two");
        (await ReceiveMessageAsync(client, cts.Token)).Should().Be("three");

        // See note in Streaming_OneFramePerYield_OrderPreserved.
    }

    private static async IAsyncEnumerable<string> ChunksAsync(
        string[] chunks,
        int delayMs,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var c in chunks)
        {
            await Task.Delay(delayMs, ct).ConfigureAwait(false);
            yield return c;
        }
    }

    private static int GetFreePort()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }
}

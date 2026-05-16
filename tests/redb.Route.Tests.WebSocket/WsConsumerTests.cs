using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.WebSocket;

namespace redb.Route.Tests.WebSocket;

/// <summary>
/// Tests for WsConsumer. Starts a real embedded WsConsumer (Kestrel-based WS server)
/// and connects to it with System.Net.WebSockets.ClientWebSocket.
/// </summary>
public class WsConsumerTests : IAsyncLifetime
{
    private WsConsumer? _consumer;
    private int _port;

    private IExchange? _lastExchange;
    private readonly List<IExchange> _capturedExchanges = [];
    private Func<IExchange, Task>? _processorAction;

    private static int GetFreePort()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    public Task InitializeAsync()
    {
        _port = GetFreePort();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_consumer is not null)
            await _consumer.Stop();
    }

    private WsConsumer CreateConsumer(bool inOut = false, int maxConnections = 0,
        WsMessageType messageType = WsMessageType.Text, string path = "/ws",
        string? subProtocol = null)
    {
        var parameters = new Dictionary<string, string> { ["messageType"] = messageType.ToString() };
        if (inOut) parameters["inOut"] = "true";
        if (maxConnections > 0) parameters["maxConnections"] = maxConnections.ToString();
        if (subProtocol is not null) parameters["subProtocol"] = subProtocol;

        var component = new WsComponent();
        var uri = new EndpointUri("ws", $"/127.0.0.1:{_port}{path}", $"ws:127.0.0.1:{_port}{path}", parameters);
        var endpoint = (WsEndpoint)component.CreateEndpoint(uri);

        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(async ci =>
            {
                var ex = ci.Arg<IExchange>();
                _lastExchange = ex;
                lock (_capturedExchanges) _capturedExchanges.Add(ex);
                if (_processorAction is not null)
                    await _processorAction(ex);
            });

        _consumer = new WsConsumer(endpoint, processor, endpoint.EndpointOptions);
        return _consumer;
    }

    private async Task<ClientWebSocket> ConnectClient(string path = "/ws", string? subProtocol = null)
    {
        var ws = new ClientWebSocket();
        if (subProtocol is not null) ws.Options.AddSubProtocol(subProtocol);
        await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{_port}{path}"), CancellationToken.None);
        return ws;
    }

    private static async Task SendText(ClientWebSocket ws, string text)
    {
        var data = Encoding.UTF8.GetBytes(text);
        await ws.SendAsync(data, WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);
    }

    private static async Task SendBinary(ClientWebSocket ws, byte[] data)
    {
        await ws.SendAsync(data, WebSocketMessageType.Binary, endOfMessage: true, CancellationToken.None);
    }

    private static async Task<string> ReceiveText(ClientWebSocket ws)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var buffer = new byte[4096];
        using var ms = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await ws.ReceiveAsync(buffer, cts.Token);
            ms.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static async Task<byte[]> ReceiveBinary(ClientWebSocket ws)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var buffer = new byte[4096];
        using var ms = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await ws.ReceiveAsync(buffer, cts.Token);
            ms.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);
        return ms.ToArray();
    }

    // ── Basic reception ──

    [Fact]
    public async Task Consumer_Text_ReceivesMessage()
    {
        var consumer = CreateConsumer();
        await consumer.Start();

        using var ws = await ConnectClient();
        await SendText(ws, "hello ws");
        await Task.Delay(200);

        _lastExchange.Should().NotBeNull();
        _lastExchange!.In.Body.Should().Be("hello ws");
    }

    [Fact]
    public async Task Consumer_Text_BodyIsString()
    {
        var consumer = CreateConsumer();
        await consumer.Start();

        using var ws = await ConnectClient();
        await SendText(ws, "typed");
        await Task.Delay(200);

        _lastExchange!.In.Body.Should().BeOfType<string>();
    }

    [Fact]
    public async Task Consumer_Binary_ReceivesMessage()
    {
        var consumer = CreateConsumer(messageType: WsMessageType.Binary);
        await consumer.Start();

        using var ws = await ConnectClient();
        var payload = new byte[] { 10, 20, 30 };
        await SendBinary(ws, payload);
        await Task.Delay(200);

        _lastExchange.Should().NotBeNull();
        _lastExchange!.In.Body.Should().BeOfType<byte[]>();
        ((byte[])_lastExchange.In.Body!).Should().BeEquivalentTo(payload);
    }

    [Fact]
    public async Task Consumer_MultipleMessages_AllProcessed()
    {
        var consumer = CreateConsumer();
        await consumer.Start();

        using var ws = await ConnectClient();
        for (var i = 0; i < 5; i++)
            await SendText(ws, $"msg-{i}");

        await Task.Delay(300);

        lock (_capturedExchanges) _capturedExchanges.Should().HaveCount(5);
        consumer.ProcessedCount.Should().Be(5);
    }

    // ── Headers ──

    [Fact]
    public async Task Consumer_SetsExchangeHeaders()
    {
        var consumer = CreateConsumer();
        await consumer.Start();

        using var ws = await ConnectClient();
        await SendText(ws, "header test");
        await Task.Delay(200);

        var h = _lastExchange!.In.Headers;
        h[WsHeaders.RemoteAddress].Should().NotBeNull();
        h[WsHeaders.LocalAddress].Should().NotBeNull();
        h[WsHeaders.ConnectionId].Should().NotBeNull();
        h[WsHeaders.MessageType].Should().Be("Text");
        h[WsHeaders.ByteCount].Should().NotBeNull();
        h[WsHeaders.Ssl].Should().Be("False");
        h[WsHeaders.Path].Should().Be("/ws");
    }

    // ── ExchangePattern ──

    [Fact]
    public async Task Consumer_InOnly_PatternIsInOnly()
    {
        var consumer = CreateConsumer(inOut: false);
        await consumer.Start();

        using var ws = await ConnectClient();
        await SendText(ws, "fire-and-forget");
        await Task.Delay(200);

        _lastExchange!.Pattern.Should().Be(ExchangePattern.InOnly);
    }

    [Fact]
    public async Task Consumer_InOut_PatternIsInOut()
    {
        _processorAction = ex =>
        {
            ex.Out = new Message("reply");
            return Task.CompletedTask;
        };

        var consumer = CreateConsumer(inOut: true);
        await consumer.Start();

        using var ws = await ConnectClient();
        await SendText(ws, "request");

        var response = await ReceiveText(ws);
        response.Should().Be("reply");
        _lastExchange!.Pattern.Should().Be(ExchangePattern.InOut);
    }

    // ── InOut response ──

    [Fact]
    public async Task Consumer_InOut_Binary_ReturnsResponse()
    {
        _processorAction = ex =>
        {
            ex.Out = new Message(new byte[] { 99, 88, 77 });
            return Task.CompletedTask;
        };

        var consumer = CreateConsumer(inOut: true, messageType: WsMessageType.Binary);
        await consumer.Start();

        using var ws = await ConnectClient();
        await SendBinary(ws, new byte[] { 1, 2, 3 });

        var response = await ReceiveBinary(ws);
        response.Should().BeEquivalentTo(new byte[] { 99, 88, 77 });
    }

    // ── Connection tracking ──

    [Fact]
    public async Task Consumer_ActiveConnections_TracksClients()
    {
        var consumer = CreateConsumer();
        await consumer.Start();

        using var ws1 = await ConnectClient();
        await Task.Delay(100);
        consumer.ActiveConnections.Should().BeGreaterThanOrEqualTo(1);

        using var ws2 = await ConnectClient();
        await Task.Delay(100);
        consumer.ActiveConnections.Should().BeGreaterThanOrEqualTo(2);
    }

    // ── ProcessedCount ──

    [Fact]
    public async Task Consumer_ProcessedCount_Increments()
    {
        var consumer = CreateConsumer();
        await consumer.Start();

        consumer.ProcessedCount.Should().Be(0);

        using var ws = await ConnectClient();
        await SendText(ws, "one");
        await Task.Delay(200);
        consumer.ProcessedCount.Should().Be(1);

        await SendText(ws, "two");
        await Task.Delay(200);
        consumer.ProcessedCount.Should().Be(2);
    }

    // ── Non-WebSocket request rejected ──

    [Fact]
    public async Task Consumer_NonWsRequest_Returns400()
    {
        var consumer = CreateConsumer();
        await consumer.Start();

        using var httpClient = new HttpClient();
        var response = await httpClient.GetAsync($"http://127.0.0.1:{_port}/ws");
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.BadRequest);
    }

    // ── Stop gracefully ──

    [Fact]
    public async Task Consumer_Stop_DisconnectsClients()
    {
        var consumer = CreateConsumer();
        await consumer.Start();

        using var ws = await ConnectClient();
        await Task.Delay(50);

        await consumer.Stop();
        _consumer = null; // prevent double-stop

        consumer.ActiveConnections.Should().Be(0);
    }

    // ── Multiple concurrent clients ──

    [Fact]
    public async Task Consumer_ConcurrentClients_AllProcessed()
    {
        var consumer = CreateConsumer();
        await consumer.Start();

        var clients = new List<ClientWebSocket>();
        for (var i = 0; i < 3; i++)
        {
            var ws = await ConnectClient();
            await SendText(ws, $"client-{i}");
            clients.Add(ws);
        }

        await Task.Delay(300);

        lock (_capturedExchanges) _capturedExchanges.Should().HaveCount(3);

        foreach (var c in clients) c.Dispose();
    }

    // ── Processor exception ──

    [Fact]
    public async Task Consumer_ProcessorThrows_SetsExceptionOnExchange()
    {
        _processorAction = _ => throw new InvalidOperationException("boom");

        var consumer = CreateConsumer();
        await consumer.Start();

        using var ws = await ConnectClient();
        await SendText(ws, "error test");
        await Task.Delay(200);

        _lastExchange.Should().NotBeNull();
        _lastExchange!.Exception.Should().BeOfType<InvalidOperationException>();
        consumer.ProcessedCount.Should().Be(1);
    }

    // ── BaseUrl ──

    [Fact]
    public async Task Consumer_BaseUrl_IsSet()
    {
        var consumer = CreateConsumer();
        await consumer.Start();

        consumer.BaseUrl.Should().NotBeNull();
        consumer.BaseUrl.Should().Contain(_port.ToString());
    }

    // ── Empty message ──

    [Fact]
    public async Task Consumer_EmptyTextMessage_Processed()
    {
        var consumer = CreateConsumer();
        await consumer.Start();

        using var ws = await ConnectClient();
        await SendText(ws, "");
        await Task.Delay(200);

        _lastExchange.Should().NotBeNull();
        _lastExchange!.In.Body.Should().Be("");
    }
}

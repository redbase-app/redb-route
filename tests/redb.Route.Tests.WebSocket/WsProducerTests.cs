using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.WebSocket;

namespace redb.Route.Tests.WebSocket;

/// <summary>
/// Tests for WsProducer. Starts a real embedded WsConsumer server
/// and connects to it with the WsProducer client.
/// </summary>
public class WsProducerTests : IAsyncLifetime
{
    private int _port;
    private WsConsumer? _serverConsumer;
    private IExchange? _lastExchange;
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
        if (_serverConsumer is not null)
            await _serverConsumer.Stop();
    }

    private async Task<WsConsumer> StartServer(bool inOut = false, WsMessageType messageType = WsMessageType.Text,
        string? subProtocol = null)
    {
        var parameters = new Dictionary<string, string> { ["messageType"] = messageType.ToString() };
        if (inOut) parameters["inOut"] = "true";
        if (subProtocol is not null) parameters["subProtocol"] = subProtocol;

        var component = new WsComponent();
        var uri = new EndpointUri("ws", $"/127.0.0.1:{_port}/ws", $"ws:127.0.0.1:{_port}/ws", parameters);
        var endpoint = (WsEndpoint)component.CreateEndpoint(uri);

        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(async ci =>
            {
                var ex = ci.Arg<IExchange>();
                _lastExchange = ex;
                if (_processorAction is not null)
                    await _processorAction(ex);
            });

        _serverConsumer = new WsConsumer(endpoint, processor, endpoint.EndpointOptions);
        await _serverConsumer.Start();
        return _serverConsumer;
    }

    private WsProducer CreateProducer(WsMessageType messageType = WsMessageType.Text, bool inOut = false,
        string? subProtocol = null, Dictionary<string, string>? extra = null)
    {
        var parameters = new Dictionary<string, string>(extra ?? [])
        {
            ["messageType"] = messageType.ToString()
        };
        if (inOut) parameters["inOut"] = "true";
        if (subProtocol is not null) parameters["subProtocol"] = subProtocol;

        var component = new WsComponent();
        var uri = new EndpointUri("ws", $"/127.0.0.1:{_port}/ws", $"ws:127.0.0.1:{_port}/ws", parameters);
        var endpoint = (WsEndpoint)component.CreateEndpoint(uri);
        return (WsProducer)endpoint.CreateProducer();
    }

    // ── Basic send ──

    [Fact]
    public async Task Producer_Text_SendsString()
    {
        await StartServer();
        var producer = CreateProducer();
        await producer.Start();

        await producer.Process(new Exchange(new Message("hello ws")));
        await Task.Delay(200);

        _lastExchange.Should().NotBeNull();
        _lastExchange!.In.Body.Should().Be("hello ws");
        await producer.Stop();
    }

    [Fact]
    public async Task Producer_Binary_SendsBytes()
    {
        await StartServer(messageType: WsMessageType.Binary);
        var producer = CreateProducer(WsMessageType.Binary);
        await producer.Start();

        var data = new byte[] { 1, 2, 3, 4, 5 };
        await producer.Process(new Exchange(new Message(data)));
        await Task.Delay(200);

        _lastExchange.Should().NotBeNull();
        _lastExchange!.In.Body.Should().BeOfType<byte[]>();
        ((byte[])_lastExchange.In.Body!).Should().BeEquivalentTo(data);
        await producer.Stop();
    }

    // ── InOut ──

    [Fact]
    public async Task Producer_InOut_ReceivesResponse()
    {
        _processorAction = ex =>
        {
            ex.Out = new Message("pong");
            return Task.CompletedTask;
        };

        await StartServer(inOut: true);
        var producer = CreateProducer(inOut: true);
        await producer.Start();

        var exchange = new Exchange(new Message("ping"));
        await producer.Process(exchange);

        exchange.Out.Should().NotBeNull();
        exchange.Out!.Body.Should().Be("pong");
        await producer.Stop();
    }

    // ── Multiple messages ──

    [Fact]
    public async Task Producer_MultipleMessages_AllReceived()
    {
        await StartServer();
        var producer = CreateProducer();
        await producer.Start();

        for (var i = 0; i < 5; i++)
            await producer.Process(new Exchange(new Message($"msg-{i}")));

        await Task.Delay(300);

        _serverConsumer!.ProcessedCount.Should().Be(5);
        await producer.Stop();
    }

    // ── Headers ──

    [Fact]
    public async Task Producer_SetsExchangeHeaders()
    {
        await StartServer();
        var producer = CreateProducer();
        await producer.Start();

        var exchange = new Exchange(new Message("test"));
        await producer.Process(exchange);

        exchange.In.Headers[WsHeaders.MessageType].Should().Be("Text");
        exchange.In.Headers[WsHeaders.Ssl].Should().Be("False");
        await producer.Stop();
    }

    // ── Lifecycle ──

    [Fact]
    public async Task Producer_ProcessBeforeStart_Throws()
    {
        var producer = CreateProducer();
        var act = () => producer.Process(new Exchange(new Message("test")));
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Producer_StartStop_Lifecycle()
    {
        await StartServer();
        var producer = CreateProducer();
        producer.IsStarted.Should().BeFalse();

        await producer.Start();
        producer.IsStarted.Should().BeTrue();

        await producer.Stop();
        producer.IsStarted.Should().BeFalse();
    }

    [Fact]
    public async Task Producer_NullBody_Sends()
    {
        await StartServer();
        var producer = CreateProducer();
        await producer.Start();

        await producer.Process(new Exchange(new Message(null)));
        await Task.Delay(200);

        _lastExchange.Should().NotBeNull();
        await producer.Stop();
    }

    [Fact]
    public async Task Producer_StreamBody_Sends()
    {
        await StartServer();
        var producer = CreateProducer();
        await producer.Start();

        var ms = new MemoryStream(Encoding.UTF8.GetBytes("stream body"));
        await producer.Process(new Exchange(new Message(ms)));
        await Task.Delay(200);

        _lastExchange!.In.Body.Should().Be("stream body");
        await producer.Stop();
    }
}

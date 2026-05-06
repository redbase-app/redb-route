using System.Net;
using System.Net.Sockets;
using System.Text;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.WebSocket;

namespace redb.Route.Tests.WebSocket;

/// <summary>
/// End-to-end integration tests: WsProducer → WsConsumer.
/// Real WebSocket connections over loopback for text and binary message types.
/// </summary>
public class WsIntegrationTests : IAsyncLifetime
{
    private int _port;
    private WsConsumer? _consumer;
    private WsProducer? _producer;

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
        if (_producer is not null) await _producer.Stop();
        if (_consumer is not null) await _consumer.Stop();
    }

    private async Task<(WsConsumer consumer, WsProducer producer)> CreatePair(
        WsMessageType messageType = WsMessageType.Text,
        bool inOut = false)
    {
        var parameters = new Dictionary<string, string> { ["messageType"] = messageType.ToString() };
        if (inOut) parameters["inOut"] = "true";

        var component = new WsComponent();

        // Consumer
        var cUri = new EndpointUri("ws", $"/127.0.0.1:{_port}/ws", $"ws:127.0.0.1:{_port}/ws", parameters);
        var cEndpoint = (WsEndpoint)component.CreateEndpoint(cUri);

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

        _consumer = new WsConsumer(cEndpoint, processor, cEndpoint.EndpointOptions);
        await _consumer.Start();

        // Producer
        var pUri = new EndpointUri("ws", $"/127.0.0.1:{_port}/ws", $"ws:127.0.0.1:{_port}/ws", parameters);
        var pEndpoint = (WsEndpoint)component.CreateEndpoint(pUri);
        _producer = (WsProducer)pEndpoint.CreateProducer();
        await _producer.Start();

        return (_consumer, _producer);
    }

    // ── Text ──

    [Fact]
    public async Task Text_ProducerToConsumer_StringDelivered()
    {
        var (consumer, producer) = await CreatePair(WsMessageType.Text);

        await producer.Process(new Exchange(new Message("integration hello")));
        await Task.Delay(200);

        _lastExchange.Should().NotBeNull();
        _lastExchange!.In.Body.Should().Be("integration hello");
    }

    [Fact]
    public async Task Text_MultipleMessages_AllDelivered()
    {
        var (consumer, producer) = await CreatePair(WsMessageType.Text);

        for (var i = 0; i < 10; i++)
            await producer.Process(new Exchange(new Message($"batch-{i}")));

        await Task.Delay(400);

        lock (_capturedExchanges) _capturedExchanges.Should().HaveCount(10);
        consumer.ProcessedCount.Should().Be(10);
    }

    // ── Binary ──

    [Fact]
    public async Task Binary_ProducerToConsumer_Delivered()
    {
        var (consumer, producer) = await CreatePair(WsMessageType.Binary);

        var data = new byte[] { 0xAA, 0xBB, 0xCC };
        await producer.Process(new Exchange(new Message(data)));
        await Task.Delay(200);

        _lastExchange.Should().NotBeNull();
        ((byte[])_lastExchange!.In.Body!).Should().BeEquivalentTo(data);
    }

    [Fact]
    public async Task Binary_MultipleMessages_AllDelivered()
    {
        var (consumer, producer) = await CreatePair(WsMessageType.Binary);

        for (var i = 0; i < 5; i++)
            await producer.Process(new Exchange(new Message(new byte[] { (byte)i })));

        await Task.Delay(300);

        lock (_capturedExchanges) _capturedExchanges.Should().HaveCount(5);
    }

    // ── InOut ──

    [Fact]
    public async Task Text_InOut_RequestResponse()
    {
        _processorAction = ex =>
        {
            var input = (string)ex.In.Body!;
            ex.Out = new Message(input.ToUpperInvariant());
            return Task.CompletedTask;
        };

        var (consumer, producer) = await CreatePair(WsMessageType.Text, inOut: true);

        var exchange = new Exchange(new Message("echo me"));
        await producer.Process(exchange);

        exchange.Out.Should().NotBeNull();
        exchange.Out!.Body.Should().Be("ECHO ME");
    }

    [Fact]
    public async Task Binary_InOut_RequestResponse()
    {
        _processorAction = ex =>
        {
            var input = (byte[])ex.In.Body!;
            var reversed = input.AsEnumerable().Reverse().ToArray();
            ex.Out = new Message(reversed);
            return Task.CompletedTask;
        };

        var (consumer, producer) = await CreatePair(WsMessageType.Binary, inOut: true);

        var data = new byte[] { 1, 2, 3, 4, 5 };
        var exchange = new Exchange(new Message(data));
        await producer.Process(exchange);

        exchange.Out.Should().NotBeNull();
        ((byte[])exchange.Out!.Body!).Should().BeEquivalentTo(new byte[] { 5, 4, 3, 2, 1 });
    }

    // ── Headers ──

    [Fact]
    public async Task Integration_ConsumerSetsAllHeaders()
    {
        var (consumer, producer) = await CreatePair();

        await producer.Process(new Exchange(new Message("hdr test")));
        await Task.Delay(200);

        var h = _lastExchange!.In.Headers;
        h[WsHeaders.RemoteAddress].Should().NotBeNull();
        h[WsHeaders.LocalAddress].Should().NotBeNull();
        h[WsHeaders.ConnectionId].Should().NotBeNull();
        h[WsHeaders.MessageType].Should().Be("Text");
        h[WsHeaders.Ssl].Should().Be("False");
        ((string)h[WsHeaders.ByteCount]!).Should().NotBeNullOrEmpty();
        h[WsHeaders.Path].Should().Be("/ws");
    }

    // ── Large payload ──

    [Fact]
    public async Task Binary_LargePayload_Delivered()
    {
        var (consumer, producer) = await CreatePair(WsMessageType.Binary);

        var largeData = new byte[64 * 1024];
        Random.Shared.NextBytes(largeData);
        await producer.Process(new Exchange(new Message(largeData)));
        await Task.Delay(500);

        _lastExchange.Should().NotBeNull();
        ((byte[])_lastExchange!.In.Body!).Should().HaveCount(64 * 1024);
    }

    // ── Null body ──

    [Fact]
    public async Task Text_NullBody_SendsEmptyFrame()
    {
        var (consumer, producer) = await CreatePair(WsMessageType.Text);

        await producer.Process(new Exchange(new Message(null)));
        await Task.Delay(200);

        _lastExchange.Should().NotBeNull();
        _lastExchange!.In.Body.Should().Be("");
    }

    // ── Stream body ──

    [Fact]
    public async Task Text_StreamBody_Delivered()
    {
        var (consumer, producer) = await CreatePair(WsMessageType.Text);

        var ms = new MemoryStream(Encoding.UTF8.GetBytes("from-stream"));
        await producer.Process(new Exchange(new Message(ms)));
        await Task.Delay(200);

        _lastExchange!.In.Body.Should().Be("from-stream");
    }
}

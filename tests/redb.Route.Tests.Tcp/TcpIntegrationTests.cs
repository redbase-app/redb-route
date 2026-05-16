using System.Net;
using System.Net.Sockets;
using System.Text;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Tcp;

namespace redb.Route.Tests.Tcp;

/// <summary>
/// End-to-end integration tests: TcpProducer → TcpConsumer.
/// Real TCP connections over loopback for all framing modes.
/// </summary>
public class TcpIntegrationTests : IAsyncLifetime
{
    private int _port;
    private TcpConsumer? _consumer;
    private TcpProducer? _producer;

    // Captured exchanges from consumer processor
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

    private (TcpConsumer consumer, TcpProducer producer) CreatePair(
        TcpFraming framing = TcpFraming.TextLine,
        bool inOut = false,
        Dictionary<string, string>? extraConsumer = null,
        Dictionary<string, string>? extraProducer = null)
    {
        // Consumer
        var cParams = new Dictionary<string, string>(extraConsumer ?? [])
        {
            ["framing"] = framing.ToString()
        };
        if (inOut) cParams["inOut"] = "true";

        var component = new TcpComponent();
        var cUri = new EndpointUri("tcp", $"/127.0.0.1:{_port}", $"tcp:127.0.0.1:{_port}", cParams);
        var cEndpoint = (TcpEndpoint)component.CreateEndpoint(cUri);

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

        _consumer = new TcpConsumer(cEndpoint, processor, cEndpoint.EndpointOptions);

        // Producer
        var pParams = new Dictionary<string, string>(extraProducer ?? [])
        {
            ["framing"] = framing.ToString()
        };
        if (inOut) pParams["inOut"] = "true";

        var pUri = new EndpointUri("tcp", $"/127.0.0.1:{_port}", $"tcp:127.0.0.1:{_port}", pParams);
        var pEndpoint = (TcpEndpoint)component.CreateEndpoint(pUri);
        _producer = (TcpProducer)pEndpoint.CreateProducer();

        return (_consumer, _producer);
    }

    // ── TextLine ──

    [Fact]
    public async Task TextLine_ProducerToConsumer_StringDelivered()
    {
        var (consumer, producer) = CreatePair(TcpFraming.TextLine);
        await consumer.Start();
        await producer.Start();

        await producer.Process(new Exchange(new Message("integration hello")));
        await Task.Delay(200);

        _lastExchange.Should().NotBeNull();
        _lastExchange!.In.Body.Should().Be("integration hello");
    }

    [Fact]
    public async Task TextLine_MultipleMessages_AllDelivered()
    {
        var (consumer, producer) = CreatePair(TcpFraming.TextLine);
        await consumer.Start();
        await producer.Start();

        for (var i = 0; i < 10; i++)
            await producer.Process(new Exchange(new Message($"batch-{i}")));

        await Task.Delay(300);

        lock (_capturedExchanges) _capturedExchanges.Should().HaveCount(10);
        consumer.ProcessedCount.Should().Be(10);
    }

    [Fact]
    public async Task TextLine_CustomDelimiter_Works()
    {
        var (consumer, producer) = CreatePair(
            TcpFraming.TextLine,
            extraConsumer: new Dictionary<string, string> { ["delimiter"] = "|" },
            extraProducer: new Dictionary<string, string> { ["delimiter"] = "|" });
        await consumer.Start();
        await producer.Start();

        await producer.Process(new Exchange(new Message("pipe")));
        await Task.Delay(200);

        _lastExchange!.In.Body.Should().Be("pipe");
    }

    // ── LengthPrefixed ──

    [Fact]
    public async Task LengthPrefixed_BinaryDelivered()
    {
        var (consumer, producer) = CreatePair(TcpFraming.LengthPrefixed);
        await consumer.Start();
        await producer.Start();

        var data = new byte[] { 0xAA, 0xBB, 0xCC, 0xDD };
        await producer.Process(new Exchange(new Message(data)));
        await Task.Delay(200);

        _lastExchange.Should().NotBeNull();
        ((byte[])_lastExchange!.In.Body!).Should().BeEquivalentTo(data);
    }

    [Fact]
    public async Task LengthPrefixed_MultipleMessages_AllDelivered()
    {
        var (consumer, producer) = CreatePair(TcpFraming.LengthPrefixed);
        await consumer.Start();
        await producer.Start();

        for (var i = 0; i < 5; i++)
            await producer.Process(new Exchange(new Message(new byte[] { (byte)i })));

        await Task.Delay(300);

        lock (_capturedExchanges) _capturedExchanges.Should().HaveCount(5);
    }

    // ── InOut request-response ──

    [Fact]
    public async Task TextLine_InOut_RequestResponse()
    {
        _processorAction = ex =>
        {
            // Echo back uppercased
            var input = (string)ex.In.Body!;
            ex.Out = new Message(input.ToUpperInvariant());
            return Task.CompletedTask;
        };

        var (consumer, producer) = CreatePair(TcpFraming.TextLine, inOut: true);
        await consumer.Start();
        await producer.Start();

        var exchange = new Exchange(new Message("hello"));
        await producer.Process(exchange);

        exchange.Out.Should().NotBeNull();
        exchange.Out!.Body.Should().Be("HELLO");
    }

    [Fact]
    public async Task LengthPrefixed_InOut_RequestResponse()
    {
        _processorAction = ex =>
        {
            var input = (byte[])ex.In.Body!;
            var reversed = input.AsEnumerable().Reverse().ToArray();
            ex.Out = new Message(reversed);
            return Task.CompletedTask;
        };

        var (consumer, producer) = CreatePair(TcpFraming.LengthPrefixed, inOut: true);
        await consumer.Start();
        await producer.Start();

        var data = new byte[] { 1, 2, 3, 4, 5 };
        var exchange = new Exchange(new Message(data));
        await producer.Process(exchange);

        exchange.Out.Should().NotBeNull();
        ((byte[])exchange.Out!.Body!).Should().BeEquivalentTo(new byte[] { 5, 4, 3, 2, 1 });
    }

    // ── Headers propagation ──

    [Fact]
    public async Task Integration_ConsumerSetsAllHeaders()
    {
        var (consumer, producer) = CreatePair();
        await consumer.Start();
        await producer.Start();

        await producer.Process(new Exchange(new Message("hdr test")));
        await Task.Delay(200);

        var h = _lastExchange!.In.Headers;
        h[TcpHeaders.RemoteAddress].Should().NotBeNull();
        h[TcpHeaders.LocalAddress].Should().NotBeNull();
        h[TcpHeaders.ConnectionId].Should().NotBeNull();
        h[TcpHeaders.Framing].Should().Be("TextLine");
        h[TcpHeaders.Ssl].Should().Be("False");
        ((string)h[TcpHeaders.ByteCount]!).Should().NotBeNullOrEmpty();
    }

    // ── Large payload ──

    [Fact]
    public async Task LengthPrefixed_LargePayload_Delivered()
    {
        var (consumer, producer) = CreatePair(TcpFraming.LengthPrefixed);
        await consumer.Start();
        await producer.Start();

        var largeData = new byte[64 * 1024];
        Random.Shared.NextBytes(largeData);
        await producer.Process(new Exchange(new Message(largeData)));
        await Task.Delay(500);

        _lastExchange.Should().NotBeNull();
        ((byte[])_lastExchange!.In.Body!).Should().HaveCount(64 * 1024);
        ((byte[])_lastExchange.In.Body!).Should().BeEquivalentTo(largeData);
    }

    // ── NullBody ──

    [Fact]
    public async Task TextLine_NullBody_SendsEmptyLine()
    {
        var (consumer, producer) = CreatePair(TcpFraming.TextLine);
        await consumer.Start();
        await producer.Start();

        await producer.Process(new Exchange(new Message(null)));
        await Task.Delay(200);

        _lastExchange.Should().NotBeNull();
        _lastExchange!.In.Body.Should().Be("");
    }

    // ── Stream body ──

    [Fact]
    public async Task TextLine_StreamBody_Delivered()
    {
        var (consumer, producer) = CreatePair(TcpFraming.TextLine);
        await consumer.Start();
        await producer.Start();

        var ms = new MemoryStream(Encoding.UTF8.GetBytes("from-stream"));
        await producer.Process(new Exchange(new Message(ms)));
        await Task.Delay(200);

        _lastExchange!.In.Body.Should().Be("from-stream");
    }
}

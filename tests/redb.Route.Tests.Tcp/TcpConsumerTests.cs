using System.Net;
using System.Net.Sockets;
using System.Text;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Tcp;

namespace redb.Route.Tests.Tcp;

/// <summary>
/// Tests for TcpConsumer. Starts a real TCP server (consumer) and sends data via TcpClient.
/// Verifies message reception, framing, exchange creation, connection tracking, and InOut pattern.
/// </summary>
public class TcpConsumerTests : IAsyncLifetime
{
    private TcpConsumer? _consumer;
    private int _port;

    // Captured exchanges from processor
    private IExchange? _lastExchange;
    private readonly List<IExchange> _capturedExchanges = [];

    // Configurable processor behaviour
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

    private TcpConsumer CreateConsumer(TcpFraming framing = TcpFraming.TextLine, bool inOut = false,
        int maxConnections = 0, Dictionary<string, string>? extra = null)
    {
        var parameters = new Dictionary<string, string>(extra ?? []);
        parameters["framing"] = framing.ToString();
        if (inOut) parameters["inOut"] = "true";
        if (maxConnections > 0) parameters["maxConnections"] = maxConnections.ToString();

        var component = new TcpComponent();
        var uri = new EndpointUri("tcp", $"/127.0.0.1:{_port}", $"tcp:127.0.0.1:{_port}", parameters);
        var endpoint = (TcpEndpoint)component.CreateEndpoint(uri);

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

        _consumer = new TcpConsumer(endpoint, processor, endpoint.EndpointOptions);
        return _consumer;
    }

    /// <summary>Sends a text line to the consumer and waits briefly for processing.</summary>
    private async Task<TcpClient> SendTextLine(string text, string delimiter = "\n")
    {
        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, _port);
        var stream = client.GetStream();
        var data = Encoding.UTF8.GetBytes(text + delimiter);
        await stream.WriteAsync(data);
        await stream.FlushAsync();
        await Task.Delay(150);
        return client;
    }

    /// <summary>Sends a length-prefixed message.</summary>
    private async Task<TcpClient> SendLengthPrefixed(byte[] payload)
    {
        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, _port);
        var stream = client.GetStream();
        var header = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header, payload.Length);
        await stream.WriteAsync(header);
        await stream.WriteAsync(payload);
        await stream.FlushAsync();
        await Task.Delay(150);
        return client;
    }

    // ── Basic reception ──

    [Fact]
    public async Task Consumer_TextLine_ReceivesMessage()
    {
        var consumer = CreateConsumer();
        await consumer.Start();

        using var client = await SendTextLine("hello");

        _lastExchange.Should().NotBeNull();
        _lastExchange!.In.Body.Should().Be("hello");
    }

    [Fact]
    public async Task Consumer_TextLine_BodyIsString()
    {
        var consumer = CreateConsumer(TcpFraming.TextLine);
        await consumer.Start();

        using var client = await SendTextLine("typed");

        _lastExchange!.In.Body.Should().BeOfType<string>();
    }

    [Fact]
    public async Task Consumer_LengthPrefixed_ReceivesMessage()
    {
        var consumer = CreateConsumer(TcpFraming.LengthPrefixed);
        await consumer.Start();

        var payload = new byte[] { 10, 20, 30 };
        using var client = await SendLengthPrefixed(payload);

        _lastExchange.Should().NotBeNull();
        _lastExchange!.In.Body.Should().BeOfType<byte[]>();
        ((byte[])_lastExchange.In.Body!).Should().BeEquivalentTo(payload);
    }

    [Fact]
    public async Task Consumer_MultipleMessages_AllProcessed()
    {
        var consumer = CreateConsumer();
        await consumer.Start();

        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, _port);
        var stream = client.GetStream();

        for (var i = 0; i < 5; i++)
        {
            var data = Encoding.UTF8.GetBytes($"msg-{i}\n");
            await stream.WriteAsync(data);
        }
        await stream.FlushAsync();
        await Task.Delay(300);

        lock (_capturedExchanges) _capturedExchanges.Should().HaveCount(5);
        consumer.ProcessedCount.Should().Be(5);
        client.Dispose();
    }

    // ── Headers ──

    [Fact]
    public async Task Consumer_SetsExchangeHeaders()
    {
        var consumer = CreateConsumer();
        await consumer.Start();

        using var client = await SendTextLine("test");

        var headers = _lastExchange!.In.Headers;
        headers[TcpHeaders.RemoteAddress].Should().NotBeNull();
        headers[TcpHeaders.LocalAddress].Should().NotBeNull();
        headers[TcpHeaders.ConnectionId].Should().NotBeNull();
        headers[TcpHeaders.Framing].Should().Be("TextLine");
        headers[TcpHeaders.ByteCount].Should().Be("4"); // "test" = 4 bytes
        headers[TcpHeaders.Ssl].Should().Be("False");
    }

    // ── ExchangePattern ──

    [Fact]
    public async Task Consumer_InOnly_PatternIsInOnly()
    {
        var consumer = CreateConsumer(inOut: false);
        await consumer.Start();

        using var client = await SendTextLine("fire-and-forget");

        _lastExchange!.Pattern.Should().Be(ExchangePattern.InOnly);
    }

    [Fact]
    public async Task Consumer_InOut_PatternIsInOut()
    {
        var consumer = CreateConsumer(inOut: true);
        await consumer.Start();

        // For InOut, processor must provide Out message
        _processorAction = ex =>
        {
            ex.Out = new Message("reply-body");
            return Task.CompletedTask;
        };

        // Send and receive response
        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, _port);
        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.UTF8.GetBytes("request\n"));
        await stream.FlushAsync();

        // Read response (TextLine framing: read until \n)
        var sb = new StringBuilder();
        var buf = new byte[1024];
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < deadline)
        {
            if (stream.DataAvailable)
            {
                var n = await stream.ReadAsync(buf);
                sb.Append(Encoding.UTF8.GetString(buf, 0, n));
                if (sb.ToString().Contains('\n')) break;
            }
            await Task.Delay(20);
        }

        sb.ToString().TrimEnd('\n').Should().Be("reply-body");
        _lastExchange!.Pattern.Should().Be(ExchangePattern.InOut);
        client.Dispose();
    }

    // ── InOut response ──

    [Fact]
    public async Task Consumer_InOut_LengthPrefixed_ReturnsResponse()
    {
        var consumer = CreateConsumer(TcpFraming.LengthPrefixed, inOut: true);
        await consumer.Start();

        _processorAction = ex =>
        {
            ex.Out = new Message(new byte[] { 99, 88, 77 });
            return Task.CompletedTask;
        };

        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, _port);
        var stream = client.GetStream();

        // Send length-prefixed message
        var payload = new byte[] { 1, 2, 3 };
        var hdr = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(hdr, payload.Length);
        await stream.WriteAsync(hdr);
        await stream.WriteAsync(payload);
        await stream.FlushAsync();

        // Read length-prefixed response
        var respHdr = new byte[4];
        var deadline = DateTime.UtcNow.AddSeconds(3);
        var totalRead = 0;
        while (totalRead < 4 && DateTime.UtcNow < deadline)
        {
            var n = await stream.ReadAsync(respHdr.AsMemory(totalRead));
            if (n == 0) break;
            totalRead += n;
        }

        var respLen = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(respHdr);
        var respData = new byte[respLen];
        totalRead = 0;
        while (totalRead < respLen && DateTime.UtcNow < deadline)
        {
            var n = await stream.ReadAsync(respData.AsMemory(totalRead));
            if (n == 0) break;
            totalRead += n;
        }

        respData.Should().BeEquivalentTo(new byte[] { 99, 88, 77 });
        client.Dispose();
    }

    // ── Connection tracking ──

    [Fact]
    public async Task Consumer_ActiveConnections_TracksClients()
    {
        var consumer = CreateConsumer();
        await consumer.Start();

        var client1 = new TcpClient();
        await client1.ConnectAsync(IPAddress.Loopback, _port);
        await Task.Delay(100);

        consumer.ActiveConnections.Should().BeGreaterThanOrEqualTo(1);

        var client2 = new TcpClient();
        await client2.ConnectAsync(IPAddress.Loopback, _port);
        await Task.Delay(100);

        consumer.ActiveConnections.Should().BeGreaterThanOrEqualTo(2);

        client1.Dispose();
        client2.Dispose();
    }

    [Fact]
    public async Task Consumer_LocalEndPoint_IsSet()
    {
        var consumer = CreateConsumer();
        await consumer.Start();

        consumer.LocalEndPoint.Should().NotBeNull();
        consumer.LocalEndPoint!.Port.Should().Be(_port);
    }

    // ── ProcessedCount ──

    [Fact]
    public async Task Consumer_ProcessedCount_Increments()
    {
        var consumer = CreateConsumer();
        await consumer.Start();

        consumer.ProcessedCount.Should().Be(0);

        using var client = await SendTextLine("one");
        consumer.ProcessedCount.Should().Be(1);

        // Send another on same connection
        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.UTF8.GetBytes("two\n"));
        await stream.FlushAsync();
        await Task.Delay(150);

        consumer.ProcessedCount.Should().Be(2);
    }

    // ── Custom delimiter ──

    [Fact]
    public async Task Consumer_CustomDelimiter_Works()
    {
        var consumer = CreateConsumer(extra: new Dictionary<string, string> { ["delimiter"] = "|" });
        await consumer.Start();

        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, _port);
        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.UTF8.GetBytes("pipe-msg|"));
        await stream.FlushAsync();
        await Task.Delay(150);

        _lastExchange!.In.Body.Should().Be("pipe-msg");
        client.Dispose();
    }

    // ── Stop gracefully ──

    [Fact]
    public async Task Consumer_Stop_DisconnectsClients()
    {
        var consumer = CreateConsumer();
        await consumer.Start();

        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, _port);
        await Task.Delay(50);

        await consumer.Stop();
        _consumer = null; // Prevent double-stop in DisposeAsync

        consumer.ActiveConnections.Should().Be(0);
        client.Dispose();
    }

    // ── Multiple concurrent clients ──

    [Fact]
    public async Task Consumer_ConcurrentClients_AllProcessed()
    {
        var consumer = CreateConsumer();
        await consumer.Start();

        var tasks = new List<Task<TcpClient>>();
        for (var i = 0; i < 3; i++)
        {
            var idx = i;
            tasks.Add(Task.Run(async () =>
            {
                var c = new TcpClient();
                await c.ConnectAsync(IPAddress.Loopback, _port);
                var s = c.GetStream();
                await s.WriteAsync(Encoding.UTF8.GetBytes($"client-{idx}\n"));
                await s.FlushAsync();
                return c;
            }));
        }

        var clients = await Task.WhenAll(tasks);
        await Task.Delay(300);

        lock (_capturedExchanges) _capturedExchanges.Should().HaveCount(3);

        foreach (var c in clients) c.Dispose();
    }

    // ── ExchangeException on processor failure ──

    [Fact]
    public async Task Consumer_ProcessorThrows_SetsExceptionOnExchange()
    {
        var consumer = CreateConsumer();
        await consumer.Start();

        _processorAction = _ => throw new InvalidOperationException("boom");

        using var client = await SendTextLine("error test");

        // Even after exception, consumer should continue running
        _lastExchange.Should().NotBeNull();
        _lastExchange!.Exception.Should().BeOfType<InvalidOperationException>();
        consumer.ProcessedCount.Should().Be(1);
    }

    // ── Empty message ──

    [Fact]
    public async Task Consumer_EmptyTextLine_Processed()
    {
        var consumer = CreateConsumer();
        await consumer.Start();

        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, _port);
        var stream = client.GetStream();
        // empty line: just the delimiter
        await stream.WriteAsync(Encoding.UTF8.GetBytes("\n"));
        await stream.FlushAsync();
        await Task.Delay(150);

        _lastExchange.Should().NotBeNull();
        _lastExchange!.In.Body.Should().Be("");
        client.Dispose();
    }
}

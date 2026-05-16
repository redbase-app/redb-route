using System.Net;
using System.Net.Sockets;
using System.Text;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Tcp;

namespace redb.Route.Tests.Tcp;

/// <summary>
/// Tests for TcpProducer using a real embedded TCP test server.
/// </summary>
public class TcpProducerTests : IAsyncLifetime
{
    private int _port;
    private TcpListener? _server;
    private CancellationTokenSource? _cts;

    // Captured by server
    private readonly List<byte[]> _receivedMessages = [];
    private string? _lastReceivedText;

    // Optional response for InOut
    private byte[]? _responseData;
    private TcpFraming _framing = TcpFraming.TextLine;
    private string _delimiter = "\n";

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
        _cts = new CancellationTokenSource();
        _server = new TcpListener(IPAddress.Loopback, _port);
        _server.Start();
        _ = AcceptLoopAsync(_cts.Token);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _cts?.Cancel();
        _server?.Stop();
        _cts?.Dispose();
        return Task.CompletedTask;
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _server!.AcceptTcpClientAsync(ct); }
            catch { break; }

            _ = HandleClientAsync(client, ct);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        try
        {
            var stream = client.GetStream();
            while (!ct.IsCancellationRequested)
            {
                var data = await TcpCodec.ReadMessageAsync(stream, _framing, _delimiter, 8192, ct);
                if (data is null) break;

                lock (_receivedMessages) _receivedMessages.Add(data);
                _lastReceivedText = Encoding.UTF8.GetString(data);

                if (_responseData is not null)
                    await TcpCodec.WriteMessageAsync(stream, _responseData, _framing, _delimiter, Encoding.UTF8, ct);
            }
        }
        catch { }
        finally { client.Dispose(); }
    }

    private TcpProducer CreateProducer(TcpFraming framing = TcpFraming.TextLine, bool inOut = false,
        Dictionary<string, string>? extra = null)
    {
        _framing = framing;
        var parameters = new Dictionary<string, string>(extra ?? []);
        parameters["framing"] = framing.ToString();
        if (inOut) parameters["inOut"] = "true";

        var component = new TcpComponent();
        var uri = new EndpointUri("tcp", $"/127.0.0.1:{_port}", $"tcp:127.0.0.1:{_port}", parameters);
        var endpoint = (TcpEndpoint)component.CreateEndpoint(uri);
        return (TcpProducer)endpoint.CreateProducer();
    }

    // ── Basic send ──

    [Fact]
    public async Task Producer_TextLine_SendsString()
    {
        var producer = CreateProducer();
        await producer.Start();

        var exchange = new Exchange(new Message("hello"));
        await producer.Process(exchange);
        await Task.Delay(100);

        _lastReceivedText.Should().Be("hello");
        await producer.Stop();
    }

    [Fact]
    public async Task Producer_TextLine_SendsBytes()
    {
        var producer = CreateProducer();
        await producer.Start();

        var data = Encoding.UTF8.GetBytes("binary hello");
        var exchange = new Exchange(new Message(data));
        await producer.Process(exchange);
        await Task.Delay(100);

        _lastReceivedText.Should().Be("binary hello");
        await producer.Stop();
    }

    [Fact]
    public async Task Producer_LengthPrefixed_SendsData()
    {
        _framing = TcpFraming.LengthPrefixed;
        var producer = CreateProducer(TcpFraming.LengthPrefixed);
        await producer.Start();

        var data = new byte[] { 1, 2, 3, 4, 5 };
        var exchange = new Exchange(new Message(data));
        await producer.Process(exchange);
        await Task.Delay(100);

        lock (_receivedMessages)
            _receivedMessages.Should().ContainSingle().Which.Should().BeEquivalentTo(data);
        await producer.Stop();
    }

    [Fact]
    public async Task Producer_Raw_SendsData()
    {
        _framing = TcpFraming.Raw;
        var producer = CreateProducer(TcpFraming.Raw);
        await producer.Start();

        var exchange = new Exchange(new Message("raw msg"));
        await producer.Process(exchange);
        await Task.Delay(100);

        lock (_receivedMessages)
            _receivedMessages.Should().HaveCountGreaterThan(0);
        await producer.Stop();
    }

    // ── InOut (request-response) ──

    [Fact]
    public async Task Producer_InOut_ReceivesResponse()
    {
        _responseData = Encoding.UTF8.GetBytes("pong");
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
        var producer = CreateProducer();
        await producer.Start();

        for (var i = 0; i < 5; i++)
        {
            var exchange = new Exchange(new Message($"msg-{i}"));
            await producer.Process(exchange);
        }

        await Task.Delay(200);
        lock (_receivedMessages) _receivedMessages.Should().HaveCount(5);
        await producer.Stop();
    }

    // ── Headers ──

    [Fact]
    public async Task Producer_SetsExchangeHeaders()
    {
        var producer = CreateProducer();
        await producer.Start();

        var exchange = new Exchange(new Message("test"));
        await producer.Process(exchange);

        exchange.In.Headers[TcpHeaders.RemoteAddress].Should().NotBeNull();
        exchange.In.Headers[TcpHeaders.LocalAddress].Should().NotBeNull();
        exchange.In.Headers[TcpHeaders.Framing].Should().Be("TextLine");
        exchange.In.Headers[TcpHeaders.Ssl].Should().Be("False");
        await producer.Stop();
    }

    // ── Lifecycle ──

    [Fact]
    public async Task Producer_ProcessBeforeStart_Throws()
    {
        var producer = CreateProducer();
        var exchange = new Exchange(new Message("test"));
        var act = () => producer.Process(exchange);
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Producer_StartStop_Lifecycle()
    {
        var producer = CreateProducer();
        producer.IsStarted.Should().BeFalse();

        await producer.Start();
        producer.IsStarted.Should().BeTrue();

        await producer.Stop();
        producer.IsStarted.Should().BeFalse();
    }

    [Fact]
    public async Task Producer_NullBody_SendsEmptyMessage()
    {
        var producer = CreateProducer();
        await producer.Start();

        var exchange = new Exchange(new Message(null));
        await producer.Process(exchange);
        await Task.Delay(100);

        lock (_receivedMessages) _receivedMessages.Should().HaveCount(1);
        await producer.Stop();
    }

    [Fact]
    public async Task Producer_StreamBody_Sends()
    {
        var producer = CreateProducer();
        await producer.Start();

        var stream = new MemoryStream(Encoding.UTF8.GetBytes("from stream"));
        var exchange = new Exchange(new Message(stream));
        await producer.Process(exchange);
        await Task.Delay(100);

        _lastReceivedText.Should().Be("from stream");
        await producer.Stop();
    }

    [Fact]
    public async Task Producer_CustomDelimiter_Works()
    {
        _delimiter = "|";
        var producer = CreateProducer(extra: new Dictionary<string, string> { ["delimiter"] = "|" });
        await producer.Start();

        var exchange = new Exchange(new Message("pipe delimited"));
        await producer.Process(exchange);
        await Task.Delay(100);

        _lastReceivedText.Should().Be("pipe delimited");
        await producer.Stop();
    }
}

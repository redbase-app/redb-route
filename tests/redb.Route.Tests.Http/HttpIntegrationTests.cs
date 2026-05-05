using System.Net;
using System.Text;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Http;

namespace redb.Route.Tests.Http;

/// <summary>
/// End-to-end integration tests: HttpProducer sending to HttpConsumer on the same machine.
/// Verifies the full roundtrip through real HTTP with Kestrel.
/// </summary>
[Collection("HttpServer")]
public class HttpIntegrationTests : IAsyncLifetime
{
    private HttpConsumer? _consumer;
    private HttpProducer? _producer;
    private int _port;
    private IExchange? _receivedExchange;
    private SharedHttpServerManager? _serverManager;

    public Task InitializeAsync()
    {
        _port = GetFreePort();
        _serverManager = new SharedHttpServerManager();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_producer is not null) await _producer.Stop();
        if (_consumer is not null) await _consumer.Stop();
        if (_serverManager is not null) await _serverManager.DisposeAsync();
    }

    private (HttpConsumer consumer, HttpProducer producer) CreatePair(
        string route = "/webhook",
        bool inOut = false,
        Dictionary<string, string>? consumerExtra = null,
        Dictionary<string, string>? producerExtra = null,
        Func<IExchange, Task>? onProcess = null)
    {
        // Consumer
        var consumerComponent = new HttpComponent();
        var consumerParams = new Dictionary<string, string>
        {
            ["host"] = "127.0.0.1",
            ["port"] = _port.ToString(),
            ["inOut"] = inOut.ToString().ToLower()
        };
        if (consumerExtra is not null)
            foreach (var (k, v) in consumerExtra)
                consumerParams[k] = v;

        var consumerPath = $"/127.0.0.1:{_port}{route}";
        var consumerUri = new EndpointUri("http", consumerPath, $"http:{consumerPath}", consumerParams);
        var consumerEndpoint = (HttpEndpoint)consumerComponent.CreateEndpoint(consumerUri);

        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(async ci =>
            {
                var ex = ci.Arg<IExchange>();
                _receivedExchange = ex;
                if (onProcess is not null)
                    await onProcess(ex);
            });

        _consumer = new HttpConsumer(consumerEndpoint, processor, consumerEndpoint.EndpointOptions, _serverManager!);

        // Producer
        var producerComponent = new HttpComponent();
        var producerParams = new Dictionary<string, string>
        {
            ["method"] = "POST"
        };
        if (producerExtra is not null)
            foreach (var (k, v) in producerExtra)
                producerParams[k] = v;

        var producerPath = $"/127.0.0.1:{_port}{route}";
        var producerUri = new EndpointUri("http", producerPath, $"http:{producerPath}", producerParams);
        var producerEndpoint = (HttpEndpoint)producerComponent.CreateEndpoint(producerUri);

        _producer = new HttpProducer(producerEndpoint, producerEndpoint.EndpointOptions);

        return (_consumer, _producer);
    }

    [Fact]
    public async Task Integration_Producer_To_Consumer_InOnly()
    {
        var (consumer, producer) = CreatePair();
        await consumer.Start();
        await producer.Start();

        var exchange = new Exchange(new Message("""{"order":"O-123","total":99.99}"""));
        exchange.In.Headers["X-Request-Id"] = "integration-test-001";

        await producer.Process(exchange);

        // Verify consumer received the request
        _receivedExchange.Should().NotBeNull();
        var receivedBody = Encoding.UTF8.GetString((byte[])_receivedExchange!.In.Body!);
        receivedBody.Should().Be("""{"order":"O-123","total":99.99}""");
        _receivedExchange.In.Headers.Should().ContainKey("X-Request-Id");

        // Verify producer got 200 response
        exchange.Out!.Headers[HttpHeaders.StatusCode].Should().Be(200);
    }

    [Fact]
    public async Task Integration_Producer_To_Consumer_InOut()
    {
        var (consumer, producer) = CreatePair(inOut: true, onProcess: ex =>
        {
            // Echo back with transformation
            var inputBody = Encoding.UTF8.GetString((byte[])ex.In.Body!);
            ex.Out = new Message($"PROCESSED: {inputBody}");
            return Task.CompletedTask;
        });
        await consumer.Start();
        await producer.Start();

        var exchange = new Exchange(new Message("hello"));
        await producer.Process(exchange);

        // Verify producer received the processed response
        var responseBody = Encoding.UTF8.GetString((byte[])exchange.Out!.Body!);
        responseBody.Should().Be("PROCESSED: hello");
        exchange.Out!.Headers[HttpHeaders.StatusCode].Should().Be(200);
    }

    [Fact]
    public async Task Integration_MultipleRequests()
    {
        var (consumer, producer) = CreatePair();
        await consumer.Start();
        await producer.Start();

        for (var i = 0; i < 5; i++)
        {
            var exchange = new Exchange(new Message($"message-{i}"));
            await producer.Process(exchange);
            exchange.Out!.Headers[HttpHeaders.StatusCode].Should().Be(200);
        }

        consumer.ProcessedCount.Should().Be(5);
    }

    [Fact]
    public async Task Integration_CustomHeaders_Roundtrip()
    {
        var (consumer, producer) = CreatePair(inOut: true, onProcess: ex =>
        {
            // Read custom header, echo it back
            var requestId = ex.In.Headers["X-Trace-Id"]?.ToString();
            ex.Out = new Message($"trace:{requestId}");
            return Task.CompletedTask;
        });
        await consumer.Start();
        await producer.Start();

        var exchange = new Exchange(new Message("trace-test"));
        exchange.In.Headers["X-Trace-Id"] = "TRACE-42";
        await producer.Process(exchange);

        var responseBody = Encoding.UTF8.GetString((byte[])exchange.Out!.Body!);
        responseBody.Should().Be("trace:TRACE-42");
    }

    // ── StreamRequest integration tests ──

    [Fact]
    public async Task Integration_StreamRequest_ConsumerReceivesStream()
    {
        Type? bodyType = null;
        string? bodyContent = null;

        var (consumer, producer) = CreatePair(
            consumerExtra: new Dictionary<string, string> { ["streamRequest"] = "true" },
            onProcess: async ex =>
            {
                bodyType = ex.In.Body?.GetType();
                var stream = (Stream)ex.In.Body!;
                using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
                bodyContent = await reader.ReadToEndAsync();
            });
        await consumer.Start();
        await producer.Start();

        var exchange = new Exchange(new Message("streamed-payload"));
        await producer.Process(exchange);

        bodyType.Should().BeAssignableTo(typeof(Stream));
        bodyContent.Should().Be("streamed-payload");
    }

    [Fact]
    public async Task Integration_StreamRequest_InOut_EchoRoundtrip()
    {
        var (consumer, producer) = CreatePair(
            inOut: true,
            consumerExtra: new Dictionary<string, string> { ["streamRequest"] = "true" },
            onProcess: async ex =>
            {
                var stream = (Stream)ex.In.Body!;
                using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
                var content = await reader.ReadToEndAsync();
                ex.Out = new Message($"ECHO:{content}");
            });
        await consumer.Start();
        await producer.Start();

        var exchange = new Exchange(new Message("stream-inout-test"));
        await producer.Process(exchange);

        var responseBody = Encoding.UTF8.GetString((byte[])exchange.Out!.Body!);
        responseBody.Should().Be("ECHO:stream-inout-test");
    }

    // ── StreamResponse integration tests ──

    [Fact]
    public async Task Integration_StreamResponse_ProducerGetsStream()
    {
        var (consumer, producer) = CreatePair(
            inOut: true,
            producerExtra: new Dictionary<string, string> { ["streamResponse"] = "true" },
            onProcess: ex =>
            {
                ex.Out = new Message("response-as-stream");
                return Task.CompletedTask;
            });
        await consumer.Start();
        await producer.Start();

        var exchange = new Exchange(new Message("trigger"));
        await producer.Process(exchange);

        exchange.Out!.Body.Should().BeAssignableTo<Stream>();
        var stream = (Stream)exchange.Out!.Body!;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var content = await reader.ReadToEndAsync();
        content.Should().Be("response-as-stream");
    }

    // ── Both StreamRequest + StreamResponse ──

    [Fact]
    public async Task Integration_StreamRequest_And_StreamResponse_FullStreaming()
    {
        var (consumer, producer) = CreatePair(
            inOut: true,
            consumerExtra: new Dictionary<string, string> { ["streamRequest"] = "true" },
            producerExtra: new Dictionary<string, string> { ["streamResponse"] = "true" },
            onProcess: async ex =>
            {
                var stream = (Stream)ex.In.Body!;
                using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
                var content = await reader.ReadToEndAsync();
                ex.Out = new Message($"STREAMED:{content}");
            });
        await consumer.Start();
        await producer.Start();

        var exchange = new Exchange(new Message("full-stream-test"));
        await producer.Process(exchange);

        exchange.Out!.Body.Should().BeAssignableTo<Stream>();
        var outStream = (Stream)exchange.Out!.Body!;
        using var outReader = new StreamReader(outStream, Encoding.UTF8);
        var result = await outReader.ReadToEndAsync();
        result.Should().Be("STREAMED:full-stream-test");
    }

    [Fact]
    public async Task Integration_StreamRequest_LargePayload_NoBuffering()
    {
        string? capturedContent = null;

        var (consumer, producer) = CreatePair(
            consumerExtra: new Dictionary<string, string> { ["streamRequest"] = "true" },
            onProcess: async ex =>
            {
                var stream = (Stream)ex.In.Body!;
                using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
                capturedContent = await reader.ReadToEndAsync();
            });
        await consumer.Start();
        await producer.Start();

        // 100KB payload
        var largePayload = new string('X', 100 * 1024);
        var exchange = new Exchange(new Message(largePayload));
        await producer.Process(exchange);

        capturedContent.Should().HaveLength(100 * 1024);
        capturedContent.Should().Be(largePayload);
    }

    private static int GetFreePort()
    {
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}

using System.Net;
using System.Text;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Grpc;

namespace redb.Route.Tests.Grpc;

/// <summary>
/// Integration tests: GrpcConsumer ← GrpcProducer round-trip.
/// </summary>
public class GrpcIntegrationTests : IAsyncLifetime
{
    private GrpcConsumer? _consumer;
    private GrpcProducer? _producer;
    private int _port;
    private IExchange? _receivedExchange;

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

    private (GrpcConsumer consumer, GrpcProducer producer) CreatePair(
        bool inOut = true,
        Func<IExchange, Task>? onProcess = null)
    {
        // Consumer (server)
        var consumerComponent = new GrpcComponent();
        var consumerParams = new Dictionary<string, string>
        {
            ["host"] = "127.0.0.1",
            ["port"] = _port.ToString(),
            ["inOut"] = inOut.ToString().ToLower()
        };
        var consumerUri = new EndpointUri("grpc", $"/127.0.0.1:{_port}", $"grpc:127.0.0.1:{_port}", consumerParams);
        var consumerEndpoint = (GrpcEndpoint)consumerComponent.CreateEndpoint(consumerUri);

        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(async ci =>
            {
                var ex = ci.Arg<IExchange>();
                _receivedExchange = ex;
                if (onProcess is not null)
                    await onProcess(ex);
            });

        _consumer = new GrpcConsumer(consumerEndpoint, processor, consumerEndpoint.EndpointOptions);

        // Producer (client)
        var producerComponent = new GrpcComponent();
        var producerParams = new Dictionary<string, string>
        {
            ["plaintext"] = "true"
        };
        var producerUri = new EndpointUri("grpc", $"/127.0.0.1:{_port}", $"grpc:127.0.0.1:{_port}", producerParams);
        var producerEndpoint = (GrpcEndpoint)producerComponent.CreateEndpoint(producerUri);

        _producer = new GrpcProducer(producerEndpoint, producerEndpoint.EndpointOptions);

        return (_consumer, _producer);
    }

    [Fact]
    public async Task Integration_InOut_RoundTrip()
    {
        var (consumer, producer) = CreatePair(inOut: true, onProcess: ex =>
        {
            var input = Encoding.UTF8.GetString((byte[])ex.In.Body!);
            ex.Out = new Message($"ECHO: {input}");
            ex.Out.Headers["processed"] = "true";
            return Task.CompletedTask;
        });
        await consumer.Start();
        await producer.Start();

        var exchange = new Exchange(new Message("hello gRPC"));
        await producer.Process(exchange);

        // Verify consumer received
        _receivedExchange.Should().NotBeNull();
        Encoding.UTF8.GetString((byte[])_receivedExchange!.In.Body!).Should().Be("hello gRPC");

        // Verify producer got response
        exchange.Out.Should().NotBeNull();
        Encoding.UTF8.GetString((byte[])exchange.Out!.Body!).Should().Be("ECHO: hello gRPC");
        exchange.Out.Headers["processed"].Should().Be("true");
    }

    [Fact]
    public async Task Integration_InOnly_ReturnsInBody()
    {
        var (consumer, producer) = CreatePair(inOut: false);
        await consumer.Start();
        await producer.Start();

        var exchange = new Exchange(new Message("fire-and-forget"));
        await producer.Process(exchange);

        // Consumer received
        _receivedExchange.Should().NotBeNull();
        Encoding.UTF8.GetString((byte[])_receivedExchange!.In.Body!).Should().Be("fire-and-forget");

        // Producer gets response (In body echoed for InOnly since no Out set)
        exchange.Out.Should().NotBeNull();
    }

    [Fact]
    public async Task Integration_BytePayload_Preserved()
    {
        var (consumer, producer) = CreatePair(inOut: true, onProcess: ex =>
        {
            // Return the payload reversed
            var input = (byte[])ex.In.Body!;
            var reversed = new byte[input.Length];
            Array.Copy(input, reversed, input.Length);
            Array.Reverse(reversed);
            ex.Out = new Message(reversed);
            return Task.CompletedTask;
        });
        await consumer.Start();
        await producer.Start();

        var payload = new byte[] { 1, 2, 3, 4, 5 };
        var exchange = new Exchange(new Message(payload));
        await producer.Process(exchange);

        ((byte[])exchange.Out!.Body!).Should().BeEquivalentTo(new byte[] { 5, 4, 3, 2, 1 });
    }

    [Fact]
    public async Task Integration_Headers_Preserved()
    {
        var (consumer, producer) = CreatePair(inOut: true, onProcess: ex =>
        {
            ex.Out = new Message("ok");
            // Echo the custom header back
            if (ex.In.Headers.ContainsKey("x-correlation-id"))
                ex.Out.Headers["x-correlation-id"] = ex.In.Headers["x-correlation-id"]!.ToString()!;
            return Task.CompletedTask;
        });
        await consumer.Start();
        await producer.Start();

        var exchange = new Exchange(new Message("test"));
        exchange.In.Headers["x-correlation-id"] = "corr-123";
        await producer.Process(exchange);

        exchange.Out.Should().NotBeNull();
        exchange.Out!.Headers["x-correlation-id"].Should().Be("corr-123");
    }

    [Fact]
    public async Task Integration_ProcessorModifies_ResponseReflects()
    {
        var (consumer, producer) = CreatePair(inOut: true, onProcess: ex =>
        {
            var body = Encoding.UTF8.GetString((byte[])ex.In.Body!);
            ex.Out = new Message($"{body} TRANSFORMED");
            ex.Out.Headers["stage"] = "enriched";
            return Task.CompletedTask;
        });
        await consumer.Start();
        await producer.Start();

        var exchange = new Exchange(new Message("raw-data"));
        await producer.Process(exchange);

        Encoding.UTF8.GetString((byte[])exchange.Out!.Body!).Should().Be("raw-data TRANSFORMED");
        exchange.Out.Headers["stage"].Should().Be("enriched");
    }

    [Fact]
    public async Task Integration_ProcessorThrows_ProducerGetsError()
    {
        var (consumer, producer) = CreatePair(inOut: true,
            onProcess: _ => throw new Exception("pipeline failure"));
        await consumer.Start();
        await producer.Start();

        var exchange = new Exchange(new Message("fail-me"));

        // The producer rethrows (ThrowOnError, default) so .OnException / retry / dead-letter see the
        // failure — the same contract as the HTTP and SOAP producers. The exchange still carries the
        // status for handlers that want to branch on it.
        var act = async () => await producer.Process(exchange);
        await act.Should().ThrowAsync<global::Grpc.Core.RpcException>();

        exchange.Exception.Should().NotBeNull();
        exchange.Exception.Should().BeOfType<global::Grpc.Core.RpcException>();
        exchange.In.Headers.Should().ContainKey(GrpcHeaders.StatusCode);
    }

    [Fact]
    public async Task Integration_MultipleRequests_AllProcessed()
    {
        int counter = 0;
        var (consumer, producer) = CreatePair(inOut: true, onProcess: ex =>
        {
            Interlocked.Increment(ref counter);
            ex.Out = new Message($"ack-{counter}");
            return Task.CompletedTask;
        });
        await consumer.Start();
        await producer.Start();

        for (int i = 0; i < 20; i++)
        {
            var exchange = new Exchange(new Message($"msg-{i}"));
            await producer.Process(exchange);
            exchange.Out.Should().NotBeNull();
        }

        counter.Should().Be(20);
        consumer.ProcessedCount.Should().Be(20);
    }

    [Fact]
    public async Task Integration_LargePayload_Works()
    {
        var (consumer, producer) = CreatePair(inOut: true, onProcess: ex =>
        {
            ex.Out = new Message(ex.In.Body);
            return Task.CompletedTask;
        });
        await consumer.Start();
        await producer.Start();

        // ~1MB payload
        var largePayload = new byte[1024 * 1024];
        Random.Shared.NextBytes(largePayload);

        var exchange = new Exchange(new Message(largePayload));
        await producer.Process(exchange);

        ((byte[])exchange.Out!.Body!).Should().BeEquivalentTo(largePayload);
    }

    [Fact]
    public async Task Integration_EmptyPayload_RoundTrip()
    {
        var (consumer, producer) = CreatePair(inOut: true, onProcess: ex =>
        {
            ex.Out = new Message("received-empty");
            return Task.CompletedTask;
        });
        await consumer.Start();
        await producer.Start();

        var exchange = new Exchange(new Message(null));
        await producer.Process(exchange);

        exchange.Out.Should().NotBeNull();
        Encoding.UTF8.GetString((byte[])exchange.Out!.Body!).Should().Be("received-empty");
    }

    // ── Expression support: deadline from exchange header ──

    [Fact]
    public async Task Integration_ExpressionDeadline_ResolvesAtRuntime()
    {
        // Create pair with deadlineExpression in producer options
        var consumerComponent = new GrpcComponent();
        var consumerParams = new Dictionary<string, string>
        {
            ["host"] = "127.0.0.1",
            ["port"] = _port.ToString(),
            ["inOut"] = "true"
        };
        var consumerUri = new EndpointUri("grpc", $"/127.0.0.1:{_port}", $"grpc:127.0.0.1:{_port}", consumerParams);
        var consumerEndpoint = (GrpcEndpoint)consumerComponent.CreateEndpoint(consumerUri);

        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var ex = ci.Arg<IExchange>();
                _receivedExchange = ex;
                ex.Out = new Message("deadline-ok");
                return Task.CompletedTask;
            });

        _consumer = new GrpcConsumer(consumerEndpoint, processor, consumerEndpoint.EndpointOptions);

        // Producer with expression deadline
        var producerComponent = new GrpcComponent();
        var producerParams = new Dictionary<string, string>
        {
            ["plaintext"] = "true",
            ["deadlineExpression"] = "${header.timeout}"
        };
        var producerUri = new EndpointUri("grpc", $"/127.0.0.1:{_port}", $"grpc:127.0.0.1:{_port}", producerParams);
        var producerEndpoint = (GrpcEndpoint)producerComponent.CreateEndpoint(producerUri);
        _producer = new GrpcProducer(producerEndpoint, producerEndpoint.EndpointOptions);

        await _consumer.Start();
        await _producer.Start();

        // Send with resolved deadline 30000ms — more than enough
        var msg = new Message("expression deadline test");
        msg.Headers["timeout"] = "30000";
        var exchange = new Exchange(msg);
        await _producer.Process(exchange);

        // The call should succeed (deadline was resolved and applied)
        exchange.Out.Should().NotBeNull();
        Encoding.UTF8.GetString((byte[])exchange.Out!.Body!).Should().Be("deadline-ok");
        _receivedExchange.Should().NotBeNull();
    }


    [Theory]
    [InlineData("trace-bin")]        // a legal HTTP header name; -bin means "binary value" to gRPC
    [InlineData("has space")]
    [InlineData("unicode-ключ")]
    public async Task A_header_that_cannot_be_a_metadata_key_does_not_kill_the_call(string headerName)
    {
        // Exchange headers are whatever upstream put there — copied HTTP request headers, values a
        // processor derived from data. gRPC metadata keys are a far narrower alphabet, and Metadata.Add
        // throws ArgumentException on anything outside it. That loop runs BEFORE the producer's try
        // block, so one odd header name took the whole call down with an exception carrying no status
        // and no route error contract. A header that cannot be represented must be dropped, not fatal.
        var (consumer, producer) = CreatePair(inOut: true, onProcess: ex =>
        {
            ex.Out = new Message("ok");
            return Task.CompletedTask;
        });
        await consumer.Start();
        await producer.Start();

        var exchange = new Exchange(new Message("payload"));
        exchange.In.Headers[headerName] = "value";
        exchange.In.Headers["x-normal"] = "kept";

        await producer.Process(exchange);

        Encoding.UTF8.GetString((byte[])exchange.Out!.Body!).Should().Be("ok");

        // The representable header still travelled: dropping the bad one must not drop the rest.
        _receivedExchange!.In.Headers.Should().ContainKey("x-normal");
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

using System.Net;
using Google.Protobuf;
using Grpc.Net.Client;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Grpc;
using redb.Route.Grpc.Proto;

namespace redb.Route.Tests.Grpc;

public class GrpcConsumerTests : IAsyncLifetime
{
    private GrpcConsumer? _consumer;
    private GrpcChannel? _channel;
    private RedbService.RedbServiceClient? _client;
    private int _port;

    private IExchange? _lastExchange;
    private readonly List<IExchange> _capturedExchanges = new();
    private Func<IExchange, Task>? _processorAction;

    public Task InitializeAsync()
    {
        _port = GetFreePort();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _channel?.Dispose();
        if (_consumer is not null) await _consumer.Stop();
    }

    private GrpcConsumer CreateConsumer(Dictionary<string, string>? extraParams = null)
    {
        var component = new GrpcComponent();
        var parameters = new Dictionary<string, string>
        {
            ["host"] = "127.0.0.1",
            ["port"] = _port.ToString()
        };
        if (extraParams is not null)
        {
            foreach (var (key, value) in extraParams)
                parameters[key] = value;
        }

        var uri = new EndpointUri("grpc", $"/127.0.0.1:{_port}", $"grpc:127.0.0.1:{_port}", parameters);
        var endpoint = (GrpcEndpoint)component.CreateEndpoint(uri);

        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(async ci =>
            {
                var ex = ci.Arg<IExchange>();
                _lastExchange = ex;
                _capturedExchanges.Add(ex);
                if (_processorAction is not null)
                    await _processorAction(ex);
            });

        _consumer = new GrpcConsumer(endpoint, processor, endpoint.EndpointOptions);
        return _consumer;
    }

    private RedbService.RedbServiceClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            EnableMultipleHttp2Connections = true
        };
        _channel = GrpcChannel.ForAddress($"http://127.0.0.1:{_port}", new GrpcChannelOptions
        {
            HttpHandler = handler
        });
        _client = new RedbService.RedbServiceClient(_channel);
        return _client;
    }

    // ── Lifecycle tests ──

    [Fact]
    public async Task Start_SetsBaseUrl()
    {
        var consumer = CreateConsumer();
        await consumer.Start();

        consumer.BaseUrl.Should().Be($"http://127.0.0.1:{_port}");
    }

    [Fact]
    public async Task Stop_CleansUp()
    {
        var consumer = CreateConsumer();
        await consumer.Start();
        await consumer.Stop();

        consumer.BaseUrl.Should().NotBeNull(); // BaseUrl remains set after stop
    }

    // ── Request handling ──

    [Fact]
    public async Task Process_ReceivesRequest_ProcessorCalled()
    {
        var consumer = CreateConsumer();
        await consumer.Start();
        var client = CreateClient();

        var request = new RedbMessage
        {
            Payload = ByteString.CopyFromUtf8("hello grpc")
        };

        await client.ProcessAsync(request);

        _lastExchange.Should().NotBeNull();
        _lastExchange!.In.Body.Should().BeOfType<byte[]>();
        System.Text.Encoding.UTF8.GetString((byte[])_lastExchange.In.Body!).Should().Be("hello grpc");
    }

    [Fact]
    public async Task Process_EmptyPayload_BodyIsNull()
    {
        var consumer = CreateConsumer();
        await consumer.Start();
        var client = CreateClient();

        var request = new RedbMessage();

        await client.ProcessAsync(request);

        _lastExchange.Should().NotBeNull();
        _lastExchange!.In.Body.Should().BeNull();
    }

    [Fact]
    public async Task Process_Headers_MappedToExchange()
    {
        var consumer = CreateConsumer();
        await consumer.Start();
        var client = CreateClient();

        var request = new RedbMessage
        {
            Payload = ByteString.CopyFromUtf8("test")
        };
        request.Headers["custom-key"] = "custom-value";
        request.Headers["another"] = "42";

        await client.ProcessAsync(request);

        _lastExchange.Should().NotBeNull();
        _lastExchange!.In.Headers["custom-key"].Should().Be("custom-value");
        _lastExchange!.In.Headers["another"].Should().Be("42");
    }

    [Fact]
    public async Task Process_GrpcHeaders_Set()
    {
        var consumer = CreateConsumer();
        await consumer.Start();
        var client = CreateClient();

        var request = new RedbMessage { Payload = ByteString.CopyFromUtf8("test") };

        await client.ProcessAsync(request);

        _lastExchange.Should().NotBeNull();
        // gRPC method header should be set
        _lastExchange!.In.Headers.Should().ContainKey(GrpcHeaders.Method);
        _lastExchange!.In.Headers[GrpcHeaders.Method].Should().NotBeNull();
        // Port should be set
        _lastExchange!.In.Headers.Should().ContainKey(GrpcHeaders.Port);
        ((int)_lastExchange!.In.Headers[GrpcHeaders.Port]!).Should().Be(_port);
    }

    [Fact]
    public async Task Process_InOut_ReturnsResponse()
    {
        _processorAction = ex =>
        {
            ex.Out = new Message("response-payload");
            ex.Out.Headers["result"] = "ok";
            return Task.CompletedTask;
        };

        var consumer = CreateConsumer();
        await consumer.Start();
        var client = CreateClient();

        var request = new RedbMessage { Payload = ByteString.CopyFromUtf8("request") };
        var response = await client.ProcessAsync(request);

        response.Payload.ToStringUtf8().Should().Be("response-payload");
        response.Headers.Should().ContainKey("result");
        response.Headers["result"].Should().Be("ok");
    }

    [Fact]
    public async Task Process_InOnly_ReturnsInBody()
    {
        var consumer = CreateConsumer(new Dictionary<string, string> { ["inOut"] = "false" });
        await consumer.Start();
        var client = CreateClient();

        var request = new RedbMessage { Payload = ByteString.CopyFromUtf8("fire-and-forget") };
        var response = await client.ProcessAsync(request);

        // InOnly: response should contain the In body (since no Out is set)
        response.Payload.ToStringUtf8().Should().Be("fire-and-forget");
    }

    [Fact]
    public async Task Process_BytePayload_PreservedCorrectly()
    {
        _processorAction = ex =>
        {
            // Echo back the same bytes
            ex.Out = new Message(ex.In.Body);
            return Task.CompletedTask;
        };

        var consumer = CreateConsumer();
        await consumer.Start();
        var client = CreateClient();

        var payload = new byte[] { 0x00, 0x01, 0x02, 0xFF, 0xFE };
        var request = new RedbMessage { Payload = ByteString.CopyFrom(payload) };
        var response = await client.ProcessAsync(request);

        response.Payload.ToByteArray().Should().BeEquivalentTo(payload);
    }

    [Fact]
    public async Task Process_ProcessorException_ReturnsRpcError()
    {
        _processorAction = _ => throw new InvalidOperationException("test error");

        var consumer = CreateConsumer();
        await consumer.Start();
        var client = CreateClient();

        var request = new RedbMessage { Payload = ByteString.CopyFromUtf8("fail") };

        var act = async () => await client.ProcessAsync(request);
        var ex = await act.Should().ThrowAsync<global::Grpc.Core.RpcException>();
        ex.Which.StatusCode.Should().Be(global::Grpc.Core.StatusCode.Internal);
        ex.Which.Status.Detail.Should().Contain("test error");
    }

    [Fact]
    public async Task Process_ProcessedCount_Increments()
    {
        var consumer = CreateConsumer();
        await consumer.Start();
        var client = CreateClient();

        consumer.ProcessedCount.Should().Be(0);

        var request = new RedbMessage { Payload = ByteString.CopyFromUtf8("test") };
        await client.ProcessAsync(request);
        consumer.ProcessedCount.Should().Be(1);

        await client.ProcessAsync(request);
        consumer.ProcessedCount.Should().Be(2);
    }

    [Fact]
    public async Task Process_GrpcMetadata_MappedToHeaders()
    {
        var consumer = CreateConsumer();
        await consumer.Start();
        var client = CreateClient();

        var metadata = new global::Grpc.Core.Metadata
        {
            { "x-request-id", "grpc-meta-123" },
            { "x-tenant", "acme" }
        };

        var request = new RedbMessage { Payload = ByteString.CopyFromUtf8("meta") };
        await client.ProcessAsync(request, metadata);

        _lastExchange.Should().NotBeNull();
        _lastExchange!.In.Headers["x-request-id"].Should().Be("grpc-meta-123");
        _lastExchange!.In.Headers["x-tenant"].Should().Be("acme");
    }

    [Fact]
    public async Task ProcessStream_InOut_ReturnsStreamedResponse()
    {
        _processorAction = ex =>
        {
            ex.Out = new Message("streamed-response");
            return Task.CompletedTask;
        };

        var consumer = CreateConsumer();
        await consumer.Start();
        var client = CreateClient();

        var request = new RedbMessage { Payload = ByteString.CopyFromUtf8("stream-request") };
        using var stream = client.ProcessStream(request);

        var items = new List<RedbMessage>();
        while (await stream.ResponseStream.MoveNext(CancellationToken.None))
        {
            items.Add(stream.ResponseStream.Current);
        }

        items.Should().HaveCount(1);
        items[0].Payload.ToStringUtf8().Should().Be("streamed-response");
    }

    [Fact]
    public async Task Process_MultipleRequests_AllProcessed()
    {
        var consumer = CreateConsumer();
        await consumer.Start();
        var client = CreateClient();

        for (int i = 0; i < 10; i++)
        {
            var request = new RedbMessage { Payload = ByteString.CopyFromUtf8($"request-{i}") };
            await client.ProcessAsync(request);
        }

        _capturedExchanges.Should().HaveCount(10);
        consumer.ProcessedCount.Should().Be(10);
    }

    [Fact]
    public async Task Process_RedbHeaders_NotCopiedToResponse()
    {
        _processorAction = ex =>
        {
            ex.Out = new Message("ok");
            ex.Out.Headers[GrpcHeaders.Method] = "should-be-stripped";
            ex.Out.Headers["keep-me"] = "yes";
            return Task.CompletedTask;
        };

        var consumer = CreateConsumer();
        await consumer.Start();
        var client = CreateClient();

        var request = new RedbMessage { Payload = ByteString.CopyFromUtf8("test") };
        var response = await client.ProcessAsync(request);

        response.Headers.Should().NotContainKey(GrpcHeaders.Method);
        response.Headers.Should().ContainKey("keep-me");
        response.Headers["keep-me"].Should().Be("yes");
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

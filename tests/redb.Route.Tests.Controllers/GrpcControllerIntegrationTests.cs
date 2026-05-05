using System.Net;
using System.Text;
using System.Text.Json;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Controllers;
using redb.Route.Grpc;

namespace redb.Route.Tests.Controllers;

/// <summary>
/// Integration tests: GrpcProducer → GrpcConsumer → GrpcControllerDispatcher → Controller → gRPC response.
/// Real gRPC connections over loopback. No mocked headers — the consumer sets method/body from the real request,
/// and the <c>dispatch-method</c> header flows through gRPC metadata.
/// </summary>
public class GrpcControllerIntegrationTests : IAsyncLifetime
{
    private GrpcConsumer? _consumer;
    private GrpcProducer? _producer;
    private int _port;

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

    /// <summary>
    /// Creates a real gRPC consumer with GrpcControllerDispatcher,
    /// and a gRPC producer (client) connected to it.
    /// </summary>
    private async Task StartPair(params Type[] controllerTypes)
    {
        var context = new RouteContext();
        var dispatcher = new GrpcControllerDispatcher(context, controllerTypes);

        // Consumer (server)
        var consumerComponent = new GrpcComponent();
        var consumerParams = new Dictionary<string, string>
        {
            ["host"] = "127.0.0.1",
            ["port"] = _port.ToString(),
            ["inOut"] = "true"
        };
        var consumerUri = new EndpointUri("grpc", $"/127.0.0.1:{_port}", $"grpc:127.0.0.1:{_port}", consumerParams);
        var consumerEndpoint = (GrpcEndpoint)consumerComponent.CreateEndpoint(consumerUri);

        _consumer = new GrpcConsumer(consumerEndpoint, dispatcher, consumerEndpoint.EndpointOptions);
        await _consumer.Start();

        // Producer (client)
        var producerComponent = new GrpcComponent();
        var producerParams = new Dictionary<string, string> { ["plaintext"] = "true" };
        var producerUri = new EndpointUri("grpc", $"/127.0.0.1:{_port}", $"grpc:127.0.0.1:{_port}", producerParams);
        var producerEndpoint = (GrpcEndpoint)producerComponent.CreateEndpoint(producerUri);

        _producer = new GrpcProducer(producerEndpoint, producerEndpoint.EndpointOptions);
        await _producer.Start();
    }

    private static IExchange CreateGrpcExchange(string method, object? body = null)
    {
        byte[]? payload = null;
        if (body is not null)
            payload = JsonSerializer.SerializeToUtf8Bytes(body);

        var exchange = new Exchange(new Message(payload ?? Array.Empty<byte>()));
        // dispatch-method header flows through gRPC metadata (no redbGrpc.* filtering)
        exchange.In.Headers[GrpcControllerDispatcher.MethodHeader] = method;
        return exchange;
    }

    // ── Basic dispatch ──────────────────────────────────

    [Fact]
    public async Task Echo_via_grpc_returns_response()
    {
        await StartPair(typeof(EchoController));

        var exchange = CreateGrpcExchange("Echo", "hello-grpc");
        await _producer!.Process(exchange);

        exchange.Out.Should().NotBeNull();
        var body = Encoding.UTF8.GetString((byte[])exchange.Out!.Body!);
        body.Should().Contain("echo:hello-grpc");
    }

    // ── No-args method ──────────────────────────────────

    [Fact]
    public async Task GetAll_via_grpc_returns_items()
    {
        await StartPair(typeof(EchoController));

        var exchange = CreateGrpcExchange("GetAll");
        await _producer!.Process(exchange);

        exchange.Out.Should().NotBeNull();
        var body = Encoding.UTF8.GetString((byte[])exchange.Out!.Body!);
        body.Should().Contain("item1");
        body.Should().Contain("item2");
    }

    // ── Complex object argument ─────────────────────────

    [Fact]
    public async Task Create_with_json_body()
    {
        await StartPair(typeof(EchoController));

        var exchange = CreateGrpcExchange("Create", new CreateModuleRequest { Name = "GrpcModule" });
        await _producer!.Process(exchange);

        exchange.Out.Should().NotBeNull();
        var body = Encoding.UTF8.GetString((byte[])exchange.Out!.Body!);
        body.Should().Contain("GrpcModule");
    }

    // ── Multiple positional args ────────────────────────

    [Fact]
    public async Task Update_with_multiple_args()
    {
        await StartPair(typeof(EchoController));

        var exchange = CreateGrpcExchange("Update",
            new object[] { 42, new CreateModuleRequest { Name = "UpdatedViaGrpc" } });
        await _producer!.Process(exchange);

        exchange.Out.Should().NotBeNull();
        var body = Encoding.UTF8.GetString((byte[])exchange.Out!.Body!);
        body.Should().Contain("UpdatedViaGrpc");
    }

    // ── Async method ────────────────────────────────────

    [Fact]
    public async Task AsyncMethod_via_grpc()
    {
        await StartPair(typeof(EchoController));

        var exchange = CreateGrpcExchange("AsyncMethod", "async-input");
        await _producer!.Process(exchange);

        exchange.Out.Should().NotBeNull();
        var body = Encoding.UTF8.GetString((byte[])exchange.Out!.Body!);
        body.Should().Contain("async:async-input");
    }

    // ── Void method returns 204 ─────────────────────────

    [Fact]
    public async Task Delete_void_method()
    {
        await StartPair(typeof(EchoController));

        var exchange = CreateGrpcExchange("Delete", 1);
        await _producer!.Process(exchange);

        // Void method — response body may be empty/null but no exception
        exchange.Exception.Should().BeNull();
        exchange.Out.Should().NotBeNull();
    }

    // ── Multi-controller qualified dispatch ─────────────

    [Fact]
    public async Task Qualified_name_dispatches_correctly()
    {
        await StartPair(typeof(EchoController), typeof(StatusController));

        var exchange = CreateGrpcExchange("Status.GetAll");
        await _producer!.Process(exchange);

        exchange.Out.Should().NotBeNull();
        var body = Encoding.UTF8.GetString((byte[])exchange.Out!.Body!);
        body.Should().Contain("status-ok");
    }

    // ── Unknown method returns error ────────────────────

    [Fact]
    public async Task Unknown_method_returns_error()
    {
        await StartPair(typeof(EchoController));

        var exchange = CreateGrpcExchange("NonExistent");
        await _producer!.Process(exchange);

        exchange.Out.Should().NotBeNull();
        var body = Encoding.UTF8.GetString((byte[])exchange.Out!.Body!);
        body.Should().Contain("NotFound");
    }

    // ── Multiple sequential requests ────────────────────

    [Fact]
    public async Task Multiple_requests_all_processed()
    {
        await StartPair(typeof(EchoController));

        for (var i = 0; i < 5; i++)
        {
            var exchange = CreateGrpcExchange("Echo", $"msg-{i}");
            await _producer!.Process(exchange);

            exchange.Out.Should().NotBeNull();
            var body = Encoding.UTF8.GetString((byte[])exchange.Out!.Body!);
            body.Should().Contain($"echo:msg-{i}");
        }
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

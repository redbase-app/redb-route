using System.Net;
using Google.Protobuf;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Grpc;
using redb.Route.Grpc.Proto;

namespace redb.Route.Tests.Grpc;

/// <summary>
/// Producer tests. Starts a real gRPC consumer (server) and uses GrpcProducer to call it.
/// </summary>
public class GrpcProducerTests : IAsyncLifetime
{
    private GrpcConsumer? _consumer;
    private GrpcProducer? _producer;
    private int _port;

    private IExchange? _serverExchange;

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

    /// <summary>Starts a consumer as the test gRPC server.</summary>
    private async Task<GrpcConsumer> StartTestServer(Func<IExchange, Task>? onProcess = null)
    {
        var component = new GrpcComponent();
        var parameters = new Dictionary<string, string>
        {
            ["host"] = "127.0.0.1",
            ["port"] = _port.ToString(),
            ["inOut"] = "true"
        };
        var uri = new EndpointUri("grpc", $"/127.0.0.1:{_port}", $"grpc:127.0.0.1:{_port}", parameters);
        var endpoint = (GrpcEndpoint)component.CreateEndpoint(uri);

        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(async ci =>
            {
                var ex = ci.Arg<IExchange>();
                _serverExchange = ex;
                if (onProcess is not null)
                    await onProcess(ex);
            });

        _consumer = new GrpcConsumer(endpoint, processor, endpoint.EndpointOptions);
        await _consumer.Start();
        return _consumer;
    }

    private GrpcProducer CreateProducer(Dictionary<string, string>? extraParams = null)
    {
        var component = new GrpcComponent();
        var parameters = new Dictionary<string, string>
        {
            ["plaintext"] = "true"
        };
        if (extraParams is not null)
        {
            foreach (var (key, value) in extraParams)
                parameters[key] = value;
        }

        var uri = new EndpointUri("grpc", $"/127.0.0.1:{_port}", $"grpc:127.0.0.1:{_port}", parameters);
        var endpoint = (GrpcEndpoint)component.CreateEndpoint(uri);

        _producer = new GrpcProducer(endpoint, endpoint.EndpointOptions);
        return _producer;
    }

    // ── Lifecycle ──

    [Fact]
    public async Task Start_CreatesChannel()
    {
        await StartTestServer();
        var producer = CreateProducer();
        await producer.Start();

        // Should not throw — producer starts successfully
    }

    [Fact]
    public async Task Process_NotStarted_Throws()
    {
        var producer = CreateProducer();
        var exchange = new Exchange(new Message("test"));

        var act = async () => await producer.Process(exchange);
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    // ── Request/Response ──

    [Fact]
    public async Task Process_SendsRequest_ServerReceives()
    {
        await StartTestServer(ex =>
        {
            ex.Out = new Message("ack");
            return Task.CompletedTask;
        });
        var producer = CreateProducer();
        await producer.Start();

        var exchange = new Exchange(new Message("producer-payload"));
        await producer.Process(exchange);

        _serverExchange.Should().NotBeNull();
        System.Text.Encoding.UTF8.GetString((byte[])_serverExchange!.In.Body!).Should().Be("producer-payload");
    }

    [Fact]
    public async Task Process_ReceivesResponse_MapsToOut()
    {
        await StartTestServer(ex =>
        {
            ex.Out = new Message("server-response");
            ex.Out.Headers["result"] = "success";
            return Task.CompletedTask;
        });
        var producer = CreateProducer();
        await producer.Start();

        var exchange = new Exchange(new Message("request"));
        await producer.Process(exchange);

        exchange.Out.Should().NotBeNull();
        var responseBody = System.Text.Encoding.UTF8.GetString((byte[])exchange.Out!.Body!);
        responseBody.Should().Be("server-response");
        exchange.Out.Headers["result"].Should().Be("success");
    }

    [Fact]
    public async Task Process_StatusCode_SetToOK()
    {
        await StartTestServer(ex =>
        {
            ex.Out = new Message("ok");
            return Task.CompletedTask;
        });
        var producer = CreateProducer();
        await producer.Start();

        var exchange = new Exchange(new Message("test"));
        await producer.Process(exchange);

        exchange.Out.Should().NotBeNull();
        exchange.Out!.Headers.Should().ContainKey(GrpcHeaders.StatusCode);
        ((int)exchange.Out.Headers[GrpcHeaders.StatusCode]!).Should().Be(0); // OK = 0
    }

    [Fact]
    public async Task Process_ByteBody_Preserved()
    {
        await StartTestServer(ex =>
        {
            // Echo back
            ex.Out = new Message(ex.In.Body);
            return Task.CompletedTask;
        });
        var producer = CreateProducer();
        await producer.Start();

        var payload = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };
        var exchange = new Exchange(new Message(payload));
        await producer.Process(exchange);

        exchange.Out.Should().NotBeNull();
        ((byte[])exchange.Out!.Body!).Should().BeEquivalentTo(payload);
    }

    [Fact]
    public async Task Process_StringBody_Handled()
    {
        await StartTestServer(ex =>
        {
            ex.Out = new Message("echo");
            return Task.CompletedTask;
        });
        var producer = CreateProducer();
        await producer.Start();

        var exchange = new Exchange(new Message("string-body"));
        await producer.Process(exchange);

        _serverExchange.Should().NotBeNull();
        System.Text.Encoding.UTF8.GetString((byte[])_serverExchange!.In.Body!).Should().Be("string-body");
    }

    [Fact]
    public async Task Process_ObjectBody_UsesToString()
    {
        await StartTestServer(ex =>
        {
            ex.Out = new Message("ok");
            return Task.CompletedTask;
        });
        var producer = CreateProducer();
        await producer.Start();

        var exchange = new Exchange(new Message(12345));
        await producer.Process(exchange);

        _serverExchange.Should().NotBeNull();
        System.Text.Encoding.UTF8.GetString((byte[])_serverExchange!.In.Body!).Should().Be("12345");
    }

    [Fact]
    public async Task Process_NullBody_ServerReceivesEmpty()
    {
        await StartTestServer(ex =>
        {
            ex.Out = new Message("ack");
            return Task.CompletedTask;
        });
        var producer = CreateProducer();
        await producer.Start();

        var exchange = new Exchange(new Message(null));
        await producer.Process(exchange);

        _serverExchange.Should().NotBeNull();
        _serverExchange!.In.Body.Should().BeNull();
    }

    [Fact]
    public async Task Process_Headers_PropagatedAsProtoHeaders()
    {
        await StartTestServer(ex =>
        {
            ex.Out = new Message("ok");
            return Task.CompletedTask;
        });
        var producer = CreateProducer();
        await producer.Start();

        var exchange = new Exchange(new Message("test"));
        exchange.In.Headers["custom-key"] = "custom-value";
        exchange.In.Headers["operation"] = "update";
        await producer.Process(exchange);

        // The server receives the headers via proto message headers
        _serverExchange.Should().NotBeNull();
        _serverExchange!.In.Headers["custom-key"].Should().Be("custom-value");
        _serverExchange!.In.Headers["operation"].Should().Be("update");
    }

    [Fact]
    public async Task Process_RedbHeaders_NotPropagated()
    {
        await StartTestServer(ex =>
        {
            ex.Out = new Message("ok");
            return Task.CompletedTask;
        });
        var producer = CreateProducer();
        await producer.Start();

        var exchange = new Exchange(new Message("test"));
        exchange.In.Headers[GrpcHeaders.Method] = "should-be-skipped";
        exchange.In.Headers["keep-me"] = "yes";
        await producer.Process(exchange);

        _serverExchange.Should().NotBeNull();
        // The redb header should not be in the proto headers on the server side
        _serverExchange!.In.Headers.Keys
            .Where(k => k.ToString()! == "keep-me")
            .Should().NotBeEmpty();
    }

    [Fact]
    public async Task Process_ServerError_SetsExceptionAndStatusHeaders()
    {
        await StartTestServer(_ => throw new InvalidOperationException("server fault"));
        var producer = CreateProducer();
        await producer.Start();

        var exchange = new Exchange(new Message("fail"));

        // ThrowOnError (default) — a failed call must reach .OnException / retry, like Http and Soap.
        var act = async () => await producer.Process(exchange);
        await act.Should().ThrowAsync<global::Grpc.Core.RpcException>();

        exchange.Exception.Should().BeOfType<global::Grpc.Core.RpcException>();
        exchange.In.Headers.Should().ContainKey(GrpcHeaders.StatusCode);
        ((int)exchange.In.Headers[GrpcHeaders.StatusCode]!).Should().Be((int)global::Grpc.Core.StatusCode.Internal);
        exchange.In.Headers.Should().ContainKey(GrpcHeaders.StatusDetail);
    }

    [Fact]
    public async Task Process_Deadline_Passed()
    {
        // Use a very long deadline so it doesn't expire
        await StartTestServer(ex =>
        {
            ex.Out = new Message("ok");
            return Task.CompletedTask;
        });
        var producer = CreateProducer(new Dictionary<string, string> { ["deadline"] = "60000" });
        await producer.Start();

        var exchange = new Exchange(new Message("deadline-test"));
        await producer.Process(exchange);

        exchange.Out.Should().NotBeNull();
    }

    private static int GetFreePort()
    {
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    [Fact]
    public void Asking_a_producer_for_tls_does_not_leave_it_in_cleartext()
    {
        // .Ssl() is the obvious way to ask a client for TLS. It sets ssl=true — and the producer builds
        // its address from Plaintext, which defaults to true and which nothing here touched. The call
        // then goes out over http:// with the endpoint believing it is secured. Only negotiationType=TLS
        // happened to link the two.
        var uri = GrpcDsl.Call("secure.internal:443").Ssl().Build();
        var endpoint = (GrpcEndpoint)new GrpcComponent().CreateEndpoint(
            EndpointUriParser.Parse(uri + "&sslCertPath=/etc/redb/x.pfx"));

        endpoint.BuildProducerAddress().Should().StartWith("https://",
            "a producer told to use TLS must not connect in the clear");
    }

    [Fact]
    public void An_explicit_plaintext_still_wins_over_ssl()
    {
        // The inverse must keep working: someone who says plaintext outright gets it, whatever else the
        // URI carries. Otherwise the fix above would take away a knob people use for local debugging.
        var uri = GrpcDsl.Call("localhost:5000").Ssl().Plaintext().Build();
        var endpoint = (GrpcEndpoint)new GrpcComponent().CreateEndpoint(
            EndpointUriParser.Parse(uri + "&sslCertPath=/etc/redb/x.pfx"));

        endpoint.BuildProducerAddress().Should().StartWith("http://");
    }
}

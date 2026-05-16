using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Controllers;
using redb.Route.SignalR;

namespace redb.Route.Tests.Controllers;

/// <summary>
/// Integration tests: SignalR client → SignalRConsumer → SignalRControllerDispatcher → Controller → response.
/// Real SignalR connections over loopback. No mocked headers — the hub bridge sets redbSignalR.Method natively.
/// </summary>
public class SignalRControllerIntegrationTests : IAsyncLifetime
{
    private int _port;
    private SignalRConsumer? _consumer;
    private SignalRProducer? _producer;

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

    /// <summary>
    /// Creates a real SignalR consumer with SignalRControllerDispatcher,
    /// and a SignalR producer (client mode) connected to it.
    /// </summary>
    private async Task StartPair(params Type[] controllerTypes)
    {
        var context = new RouteContext();
        var dispatcher = new SignalRControllerDispatcher(context, controllerTypes);

        var component = new SignalRComponent();

        // Consumer (hub server)
        var consumerParams = new Dictionary<string, string> { ["inOut"] = "true" };
        var cPath = $"/127.0.0.1:{_port}/hub";
        var cUri = new EndpointUri("signalr", cPath, $"signalr:{cPath}", consumerParams);
        var cEndpoint = (SignalREndpoint)component.CreateEndpoint(cUri);

        _consumer = new SignalRConsumer(cEndpoint, dispatcher, cEndpoint.EndpointOptions);
        await _consumer.Start();

        // Producer (client mode, InOut for request-response)
        var producerParams = new Dictionary<string, string>
        {
            ["mode"] = "Client",
            ["inOut"] = "true"
        };
        var pPath = $"/127.0.0.1:{_port}/hub";
        var pUri = new EndpointUri("signalr", pPath, $"signalr:{pPath}", producerParams);
        var pEndpoint = (SignalREndpoint)component.CreateEndpoint(pUri);
        _producer = (SignalRProducer)pEndpoint.CreateProducer();
        await _producer.Start();

        // Wait for connection to be established
        await Task.Delay(300);
    }

    // ── Basic method dispatch ──────────────────────────

    [Fact]
    public async Task Echo_method_returns_echo_response()
    {
        await StartPair(typeof(EchoController));

        var exchange = new Exchange(new Message("hello-world"));
        exchange.In.setHeader(SignalRHeaders.Method, "Echo");
        await _producer!.Process(exchange);

        exchange.Out.Should().NotBeNull();
        exchange.Out!.Body.Should().NotBeNull();
        exchange.Out!.Body!.ToString().Should().Be("echo:hello-world");
    }

    // ── No-args method ──────────────────────────────────

    [Fact]
    public async Task GetAll_method_returns_items()
    {
        await StartPair(typeof(EchoController));

        var exchange = new Exchange(new Message(null));
        exchange.In.setHeader(SignalRHeaders.Method, "GetAll");
        await _producer!.Process(exchange);

        exchange.Out.Should().NotBeNull();
        exchange.Out!.Body.Should().NotBeNull();
        // SignalR returns the serialized result
        var body = exchange.Out!.Body!.ToString();
        body.Should().Contain("item1");
        body.Should().Contain("item2");
    }

    // ── Method with complex argument ────────────────────

    [Fact]
    public async Task Create_method_with_object_arg()
    {
        await StartPair(typeof(EchoController));

        var request = new CreateModuleRequest { Name = "TestModule" };
        var exchange = new Exchange(new Message(request));
        exchange.In.setHeader(SignalRHeaders.Method, "Create");
        await _producer!.Process(exchange);

        exchange.Out.Should().NotBeNull();
        exchange.Out!.Body.Should().NotBeNull();
        var body = exchange.Out!.Body!.ToString();
        body.Should().Contain("TestModule");
    }

    // ── Async method ────────────────────────────────────

    [Fact]
    public async Task AsyncMethod_returns_async_result()
    {
        await StartPair(typeof(EchoController));

        var exchange = new Exchange(new Message("test-input"));
        exchange.In.setHeader(SignalRHeaders.Method, "AsyncMethod");
        await _producer!.Process(exchange);

        exchange.Out.Should().NotBeNull();
        exchange.Out!.Body!.ToString().Should().Be("async:test-input");
    }

    // ── Multi-controller dispatch ───────────────────────

    [Fact]
    public async Task Qualified_name_reaches_correct_controller()
    {
        await StartPair(typeof(EchoController), typeof(StatusController));

        // Use qualified name to reach StatusController.GetAll
        var exchange = new Exchange(new Message(null));
        exchange.In.setHeader(SignalRHeaders.Method, "Status.GetAll");
        await _producer!.Process(exchange);

        exchange.Out.Should().NotBeNull();
        exchange.Out!.Body!.ToString().Should().Be("status-ok");
    }

    // ── Multiple sequential requests ────────────────────

    [Fact]
    public async Task Multiple_requests_all_processed()
    {
        await StartPair(typeof(EchoController));

        for (var i = 0; i < 5; i++)
        {
            var exchange = new Exchange(new Message($"msg-{i}"));
            exchange.In.setHeader(SignalRHeaders.Method, "Echo");
            await _producer!.Process(exchange);

            exchange.Out.Should().NotBeNull();
            exchange.Out!.Body!.ToString().Should().Be($"echo:msg-{i}");
        }
    }
}

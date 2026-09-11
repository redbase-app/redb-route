using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR.Client;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Http;
using redb.Route.SignalR;

namespace redb.Route.Tests.SignalR;

/// <summary>
/// Ф14 волна 7, findings of the review of the phase itself: a hub is an occupant of the shared
/// listener like any HTTP route, and it has to survive its neighbours coming and going, survive a
/// restart of its own, and be told apart from a hub with the same path on another port.
/// </summary>
public sealed class SignalRLifecycleTests : IAsyncLifetime
{
    private readonly SharedHttpServerManager _manager = new();
    private readonly HttpClient _http = new();
    private readonly List<IConsumer> _consumers = [];
    private readonly List<IAsyncDisposable> _connections = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var c in _connections) await c.DisposeAsync();
        foreach (var c in _consumers) await c.Stop();
        _http.Dispose();
        await _manager.DisposeAsync();
    }

    private static int GetFreePort()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private static SignalREndpoint Endpoint(SignalRComponent component, string path)
        => (SignalREndpoint)component.CreateEndpoint(EndpointUriParser.Parse($"signalr://{path}?inOut=true"));

    private async Task<HubConnection> ConnectAsync(int port, string hubPath)
    {
        var connection = new HubConnectionBuilder().WithUrl($"http://127.0.0.1:{port}{hubPath}").Build();
        _connections.Add(connection);
        await connection.StartAsync();
        return connection;
    }

    [Fact]
    public async Task StoppingTheLastHttpRoute_LeavesALiveHubAlone()
    {
        var port = GetFreePort();
        var registration = _manager.RegisterRoute("127.0.0.1", port, "/api/health", "GET",
            ctx => ctx.Response.WriteAsync("healthy"));

        var component = new SignalRComponent { ServerManager = _manager };
        var endpoint = Endpoint(component, $"127.0.0.1:{port}/hub");
        await endpoint.Start();
        var consumer = endpoint.CreateConsumer(new EchoProcessor());
        _consumers.Add(consumer);
        await consumer.Start();

        // The HTTP consumer on this port shuts down; the hub is still serving.
        _manager.UnregisterRoute(registration);
        await _manager.StopIfEmpty("127.0.0.1", port);

        var connection = await ConnectAsync(port, "/hub");
        var reply = await connection.InvokeAsync<object?>("Invoke", "Send", new object?[] { "alive" });
        reply!.ToString().Should().Contain("alive",
            "хаб — такой же жилец слушателя, как HTTP-маршрут: уход соседа не должен его выселять");
    }

    [Fact]
    public async Task StoppingOneOfTwoHubs_LeavesTheOtherServing()
    {
        var port = GetFreePort();
        var component = new SignalRComponent { ServerManager = _manager };

        var ordersEndpoint = Endpoint(component, $"127.0.0.1:{port}/orders");
        var alertsEndpoint = Endpoint(component, $"127.0.0.1:{port}/alerts");
        await ordersEndpoint.Start();
        await alertsEndpoint.Start();

        var orders = ordersEndpoint.CreateConsumer(new TagProcessor("orders"));
        var alerts = alertsEndpoint.CreateConsumer(new TagProcessor("alerts"));
        _consumers.Add(alerts);
        await orders.Start();
        await alerts.Start();

        await orders.Stop();

        var connection = await ConnectAsync(port, "/alerts");
        (await connection.InvokeAsync<object?>("Invoke", "X", new object?[] { "b" }))!.ToString()
            .Should().Be("alerts:b", "остановка одного хаба не должна ронять слушатель второго");
    }

    [Fact]
    public async Task ConsumerRestart_ServesAgain()
    {
        var port = GetFreePort();
        var component = new SignalRComponent { ServerManager = _manager };
        var endpoint = Endpoint(component, $"127.0.0.1:{port}/hub");
        await endpoint.Start();

        var consumer = endpoint.CreateConsumer(new EchoProcessor());
        _consumers.Add(consumer);
        await consumer.Start();
        await consumer.Stop();

        await consumer.Start();

        var connection = await ConnectAsync(port, "/hub");
        var reply = await connection.InvokeAsync<object?>("Invoke", "Send", new object?[] { "again" });
        reply!.ToString().Should().Contain("again",
            "перезапуск маршрута обязан поднимать хаб заново, а не падать на «No routes registered»");
    }

    [Fact]
    public async Task SameHubPathOnTwoPorts_EachServesItsOwnConsumer()
    {
        var alphaPort = GetFreePort();
        var betaPort = GetFreePort();
        var component = new SignalRComponent { ServerManager = _manager };

        var alphaEndpoint = Endpoint(component, $"127.0.0.1:{alphaPort}/hub");
        var betaEndpoint = Endpoint(component, $"127.0.0.1:{betaPort}/hub");
        await alphaEndpoint.Start();
        await betaEndpoint.Start();

        var alpha = alphaEndpoint.CreateConsumer(new TagProcessor("alpha"));
        var beta = betaEndpoint.CreateConsumer(new TagProcessor("beta"));
        _consumers.Add(alpha);
        _consumers.Add(beta);
        await alpha.Start();
        await beta.Start();

        var toAlpha = await ConnectAsync(alphaPort, "/hub");
        var toBeta = await ConnectAsync(betaPort, "/hub");

        (await toAlpha.InvokeAsync<object?>("Invoke", "X", new object?[] { "1" }))!.ToString()
            .Should().Be("alpha:1", "диспетчеризация только по пути путает хабы на разных портах");
        (await toBeta.InvokeAsync<object?>("Invoke", "X", new object?[] { "1" }))!.ToString()
            .Should().Be("beta:1");
    }

    private sealed class EchoProcessor : IProcessor
    {
        public Task Process(IExchange exchange, CancellationToken ct = default)
        {
            exchange.Out = new Message("echo:" + exchange.In.Body);
            return Task.CompletedTask;
        }
    }

    private sealed class TagProcessor(string tag) : IProcessor
    {
        public Task Process(IExchange exchange, CancellationToken ct = default)
        {
            exchange.Out = new Message($"{tag}:{exchange.In.Body}");
            return Task.CompletedTask;
        }
    }
}

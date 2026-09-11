using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using Microsoft.AspNetCore.Http;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Http;
using redb.Route.WebSocket;

namespace redb.Route.Tests.WebSocket;

/// <summary>
/// Ф14 волна 1: the WebSocket consumer serves on the SHARED Kestrel, so a ws route and a plain
/// HTTP route can live on one port — the thing that was impossible while every consumer built
/// its own server (REST and WebSocket behind one reverse proxy on one port).
/// </summary>
public sealed class WsSharedHostTests : IAsyncLifetime
{
    private readonly SharedHttpServerManager _manager = new();
    private readonly HttpClient _http = new();
    private WsConsumer? _consumer;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_consumer is not null) await _consumer.Stop();
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

    private WsEndpoint CreateEndpoint(int port, string path, string? extraParams = null)
    {
        var component = new WsComponent { ServerManager = _manager };
        var uri = EndpointUriParser.Parse(
            $"ws://127.0.0.1:{port}{path}" + (extraParams is null ? "" : $"?{extraParams}"));
        return (WsEndpoint)component.CreateEndpoint(uri);
    }

    [Fact]
    public async Task WebSocketAndHttp_ShareOnePort()
    {
        var port = GetFreePort();

        // A plain HTTP route registered by "another connector" on the same listener.
        _manager.RegisterRoute("127.0.0.1", port, "/api/health", "GET",
            ctx => ctx.Response.WriteAsync("healthy"));

        var endpoint = CreateEndpoint(port, "/chat", "inOut=true");
        await endpoint.Start();
        _consumer = (WsConsumer)endpoint.CreateConsumer(new EchoProcessor());
        await _consumer.Start();

        _manager.ServerCount.Should().Be(1, "ws и http обязаны делить ОДИН Kestrel");

        // HTTP still works…
        (await _http.GetStringAsync($"http://127.0.0.1:{port}/api/health")).Should().Be("healthy");

        // …and the WebSocket upgrade on the same port works too.
        using var client = new ClientWebSocket();
        await client.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/chat"), CancellationToken.None);
        await client.SendAsync(Encoding.UTF8.GetBytes("hello"), WebSocketMessageType.Text, true, CancellationToken.None);

        var buffer = new byte[64];
        var result = await client.ReceiveAsync(buffer, CancellationToken.None);
        Encoding.UTF8.GetString(buffer, 0, result.Count).Should().Be("echo:hello");
    }

    [Fact]
    public async Task NonWebSocketRequestToWsPath_Gets400()
    {
        var port = GetFreePort();
        var endpoint = CreateEndpoint(port, "/chat");
        await endpoint.Start();
        _consumer = (WsConsumer)endpoint.CreateConsumer(new EchoProcessor());
        await _consumer.Start();

        var response = await _http.GetAsync($"http://127.0.0.1:{port}/chat");
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ConsumerStop_LeavesListenerAliveForOtherRoutes()
    {
        var port = GetFreePort();
        _manager.RegisterRoute("127.0.0.1", port, "/api/health", "GET",
            ctx => ctx.Response.WriteAsync("healthy"));

        var endpoint = CreateEndpoint(port, "/chat");
        await endpoint.Start();
        var consumer = (WsConsumer)endpoint.CreateConsumer(new EchoProcessor());
        await consumer.Start();
        await consumer.Stop();

        // The HTTP route on the same listener must survive the ws consumer going away.
        (await _http.GetStringAsync($"http://127.0.0.1:{port}/api/health")).Should().Be("healthy");
    }

    [Fact]
    public async Task ConsumerRecordsEndpointStatistics()
    {
        var port = GetFreePort();
        var endpoint = CreateEndpoint(port, "/stats");
        await endpoint.Start();
        _consumer = (WsConsumer)endpoint.CreateConsumer(new EchoProcessor());
        await _consumer.Start();

        using var client = new ClientWebSocket();
        await client.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/stats"), CancellationToken.None);
        await client.SendAsync(Encoding.UTF8.GetBytes("12345"), WebSocketMessageType.Text, true, CancellationToken.None);

        // Ownership audit: pipeline statistics (MessagesIn/BytesIn) are recorded by the core's
        // StatisticsProcessor around a ROUTED consumer - self-recording here double-counted them.
        // A hand-built consumer therefore stays at zero; the routed contract is pinned by
        // WsStatisticsOwnershipTests.RoutedConsumer_CountsEachFrameOnce.
        await Task.Delay(500);
        var stats = (IEndpointStatistics)endpoint;
        stats.MessagesIn.Should().Be(0, "MessagesIn пишет ядро вокруг маршрутного консьюмера");
        stats.BytesIn.Should().Be(0);
    }

    private sealed class EchoProcessor : IProcessor
    {
        public Task Process(IExchange exchange, CancellationToken ct = default)
        {
            exchange.Out = new Message("echo:" + exchange.In.Body);
            return Task.CompletedTask;
        }
    }
}

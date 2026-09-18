using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Http;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Http;
using redb.Route.WebSocket;

namespace redb.Route.Tests.WebSocket;

/// <summary>
/// The caller's identity on WebSocket exchanges. The shared host's resolver identifies the handshake and
/// the consumer puts the principal on every exchange the socket produces; the component's own
/// <see cref="WsComponent.Authenticate"/>, when set, decides instead.
/// </summary>
public sealed class WsPrincipalTests : IAsyncLifetime
{
    private readonly List<IConsumer> _consumers = [];
    private readonly List<SharedHttpServerManager> _managers = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var c in _consumers) await c.Stop();
        foreach (var m in _managers) await m.DisposeAsync();
    }

    private static int GetFreePort()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private static ClaimsPrincipal User(string id) =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, id)], "test"));

    private static Func<HttpContext, Task<ClaimsPrincipal?>> TokenIdentifies(string id) => ctx =>
        Task.FromResult(ctx.Request.Query["access_token"].ToString() == "good" ? User(id) : null);

    private SharedHttpServerManager Manager(Func<HttpContext, Task<ClaimsPrincipal?>> resolve)
    {
        var manager = new SharedHttpServerManager(new HttpHostingOptions { ResolvePrincipal = resolve });
        _managers.Add(manager);
        return manager;
    }

    private async Task<IExchange> SendOneFrameAsync(WsComponent component, int port)
    {
        var uri = new EndpointUri(component.Scheme, $"/127.0.0.1:{port}/who", $"{component.Scheme}:127.0.0.1:{port}/who", new Dictionary<string, string>());
        var endpoint = (WsEndpoint)component.CreateEndpoint(uri);
        var captured = new CapturingProcessor();
        var consumer = (WsConsumer)endpoint.CreateConsumer(captured);
        _consumers.Add(consumer);
        await consumer.Start();

        using var client = new ClientWebSocket();
        await client.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/who?access_token=good"), CancellationToken.None);
        await client.SendAsync(Encoding.UTF8.GetBytes("hi"), WebSocketMessageType.Text, true, CancellationToken.None);

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (captured.LastExchange is null && DateTime.UtcNow < deadline)
            await Task.Delay(20);

        captured.LastExchange.Should().NotBeNull();
        return captured.LastExchange!;
    }

    [Fact]
    public async Task HostResolver_PrincipalAndUserIdReachTheExchange()
    {
        var port = GetFreePort();
        var component = new WsComponent { ServerManager = Manager(TokenIdentifies("host-user")) };

        var exchange = await SendOneFrameAsync(component, port);

        ExchangePrincipal.Get(exchange)!.FindFirst(ClaimTypes.NameIdentifier)!.Value.Should().Be("host-user");
        exchange.In.GetHeader<string>(WsHeaders.UserId).Should().Be("host-user",
            "the header is derived from the same principal, whoever produced it");
    }

    [Fact]
    public async Task ComponentAuthenticate_TakesPrecedenceOverTheHostResolver()
    {
        var port = GetFreePort();
        var component = new WsComponent
        {
            ServerManager = Manager(_ => Task.FromResult<ClaimsPrincipal?>(User("host-user"))),
            Authenticate = TokenIdentifies("ws-user"),
        };

        var exchange = await SendOneFrameAsync(component, port);

        ExchangePrincipal.Get(exchange)!.FindFirst(ClaimTypes.NameIdentifier)!.Value.Should().Be("ws-user",
            "the transport's own hook is the more specific decision on its path");
    }

    private sealed class CapturingProcessor : IProcessor
    {
        public IExchange? LastExchange { get; private set; }

        public Task Process(IExchange exchange, CancellationToken ct = default)
        {
            LastExchange = exchange.Snapshot();
            return Task.CompletedTask;
        }
    }
}

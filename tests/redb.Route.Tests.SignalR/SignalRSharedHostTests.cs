using System.Net;
using System.Net.Sockets;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Http;
using redb.Route.SignalR;

namespace redb.Route.Tests.SignalR;

/// <summary>
/// Ф14 волны 1, 2 и 4 end to end: the hub serves on the SHARED Kestrel (so <c>/hub/</c> and the
/// REST API can sit on one port behind one proxy), a host-supplied delegate authenticates the
/// handshake and fills UserIdentifier, and MessagePack actually negotiates.
/// </summary>
public sealed class SignalRSharedHostTests : IAsyncLifetime
{
    private readonly SharedHttpServerManager _manager = new();
    private readonly HttpClient _http = new();
    private readonly List<IAsyncDisposable> _connections = [];
    private SignalRConsumer? _consumer;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var c in _connections) await c.DisposeAsync();
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

    private SignalREndpoint CreateEndpoint(SignalRComponent component, int port, string hubPath, string? extraParams = null)
    {
        var uri = EndpointUriParser.Parse(
            $"signalr://127.0.0.1:{port}{hubPath}" + (extraParams is null ? "" : $"?{extraParams}"));
        return (SignalREndpoint)component.CreateEndpoint(uri);
    }

    private async Task<HubConnection> ConnectAsync(int port, string hubPath,
        Action<IHubConnectionBuilder>? configure = null, string? accessToken = null)
    {
        var builder = new HubConnectionBuilder().WithUrl($"http://127.0.0.1:{port}{hubPath}", o =>
        {
            if (accessToken is not null)
                o.AccessTokenProvider = () => Task.FromResult<string?>(accessToken);
        });
        configure?.Invoke(builder);

        var connection = builder.Build();
        _connections.Add(connection);
        await connection.StartAsync();
        return connection;
    }

    // ── Волна 1: one port for the hub and the REST API ──

    [Fact]
    public async Task HubAndHttp_ShareOnePort()
    {
        var port = GetFreePort();
        _manager.RegisterRoute("127.0.0.1", port, "/api/health", "GET",
            ctx => ctx.Response.WriteAsync("healthy"));

        var component = new SignalRComponent { ServerManager = _manager };
        var endpoint = CreateEndpoint(component, port, "/chatHub", "inOut=true");
        await endpoint.Start();
        _consumer = (SignalRConsumer)endpoint.CreateConsumer(new EchoProcessor());
        await _consumer.Start();

        _manager.ServerCount.Should().Be(1, "хаб и HTTP обязаны делить ОДИН Kestrel");
        (await _http.GetStringAsync($"http://127.0.0.1:{port}/api/health")).Should().Be("healthy");

        var connection = await ConnectAsync(port, "/chatHub");
        var reply = await connection.InvokeAsync<object?>("Invoke", "Send", new object?[] { "ping" });
        reply!.ToString().Should().Contain("ping");
    }

    [Fact]
    public async Task TwoHubsOnOnePort_EachRoutesToItsOwnConsumer()
    {
        var port = GetFreePort();
        var component = new SignalRComponent { ServerManager = _manager };

        var ordersEndpoint = CreateEndpoint(component, port, "/orders", "inOut=true");
        var alertsEndpoint = CreateEndpoint(component, port, "/alerts", "inOut=true");
        await ordersEndpoint.Start();
        await alertsEndpoint.Start();

        var ordersConsumer = (SignalRConsumer)ordersEndpoint.CreateConsumer(new TagProcessor("orders"));
        var alertsConsumer = (SignalRConsumer)alertsEndpoint.CreateConsumer(new TagProcessor("alerts"));
        await ordersConsumer.Start();
        await alertsConsumer.Start();

        try
        {
            var toOrders = await ConnectAsync(port, "/orders");
            var toAlerts = await ConnectAsync(port, "/alerts");

            (await toOrders.InvokeAsync<object?>("Invoke", "X", new object?[] { "a" }))!.ToString()
                .Should().Be("orders:a", "хаб обязан находить своего консьюмера по пути запроса");
            (await toAlerts.InvokeAsync<object?>("Invoke", "X", new object?[] { "b" }))!.ToString()
                .Should().Be("alerts:b");
        }
        finally
        {
            await ordersConsumer.Stop();
            await alertsConsumer.Stop();
        }
    }

    // ── Волна 2: authentication supplied by the host ──

    /// <summary>
    /// The recipe a host is expected to follow: the SignalR client puts the token into the
    /// <c>Authorization</c> header on negotiate and into the <c>access_token</c> query parameter on
    /// the WebSocket upgrade (a browser cannot set headers there), so both have to be read.
    /// </summary>
    private static Func<HttpContext, Task<ClaimsPrincipal?>> TokenAuth(string expected) => ctx =>
    {
        var header = ctx.Request.Headers.Authorization.ToString();
        var token = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? header["Bearer ".Length..]
            : ctx.Request.Query["access_token"].ToString();

        return Task.FromResult<ClaimsPrincipal?>(token == expected
            ? new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "user-42")], "test"))
            : null);
    };

    [Fact]
    public async Task Authenticate_RejectsHandshakeWithoutToken()
    {
        var port = GetFreePort();
        var component = new SignalRComponent
        {
            ServerManager = _manager,
            Authenticate = TokenAuth("good"),
        };

        var endpoint = CreateEndpoint(component, port, "/secure");
        await endpoint.Start();
        _consumer = (SignalRConsumer)endpoint.CreateConsumer(new EchoProcessor());
        await _consumer.Start();

        var act = () => ConnectAsync(port, "/secure");
        await act.Should().ThrowAsync<Exception>("без токена рукопожатие обязано отклоняться");
    }

    [Fact]
    public async Task Authenticate_FillsUserIdHeader()
    {
        var port = GetFreePort();
        var component = new SignalRComponent
        {
            ServerManager = _manager,
            Authenticate = TokenAuth("good"),
        };

        var endpoint = CreateEndpoint(component, port, "/secure", "inOut=true");
        await endpoint.Start();
        var captured = new CapturingProcessor();
        _consumer = (SignalRConsumer)endpoint.CreateConsumer(captured);
        await _consumer.Start();

        var connection = await ConnectAsync(port, "/secure", accessToken: "good");
        await connection.InvokeAsync<object?>("Invoke", "Send", new object?[] { "hello" });

        captured.LastExchange.Should().NotBeNull();
        captured.LastExchange!.In.GetHeader<string>(SignalRHeaders.UserId)
            .Should().Be("user-42", "UserIdentifier берётся из HttpContext.User, который ставит наш делегат");
    }

    // ── The caller's identity on the exchange ──

    private static string TokenOf(HttpContext ctx)
    {
        var header = ctx.Request.Headers.Authorization.ToString();
        return header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? header["Bearer ".Length..]
            : ctx.Request.Query["access_token"].ToString();
    }

    private static ClaimsPrincipal User(string id) =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, id)], "test"));

    private async Task<IExchange> InvokeThroughAsync(SignalRComponent component, int port, string? accessToken)
    {
        var endpoint = CreateEndpoint(component, port, "/who", "inOut=true");
        await endpoint.Start();
        var captured = new CapturingProcessor();
        _consumer = (SignalRConsumer)endpoint.CreateConsumer(captured);
        await _consumer.Start();

        var connection = await ConnectAsync(port, "/who", accessToken: accessToken);
        await connection.InvokeAsync<object?>("Invoke", "Send", new object?[] { "hello" });

        captured.LastExchange.Should().NotBeNull();
        return captured.LastExchange!;
    }

    [Fact]
    public async Task HostResolver_PrincipalAndUserIdentifierReachTheExchange()
    {
        var port = GetFreePort();
        await using var manager = new SharedHttpServerManager(new HttpHostingOptions
        {
            ResolvePrincipal = ctx => Task.FromResult(TokenOf(ctx) == "good" ? User("host-user") : null),
        });

        try
        {
            var exchange = await InvokeThroughAsync(new SignalRComponent { ServerManager = manager }, port, "good");

            ExchangePrincipal.Get(exchange)!.FindFirst(ClaimTypes.NameIdentifier)!.Value.Should().Be("host-user");
            exchange.In.GetHeader<string>(SignalRHeaders.UserId).Should().Be("host-user",
                "the host's principal is handed to SignalR, so UserIdentifier and Clients.User work too");
        }
        finally
        {
            foreach (var c in _connections) await c.DisposeAsync();
            _connections.Clear();
            await _consumer!.Stop();
            _consumer = null;
        }
    }

    [Fact]
    public async Task AnonymousHubConnection_CarriesNoIdentity()
    {
        var port = GetFreePort();
        await using var manager = new SharedHttpServerManager(new HttpHostingOptions
        {
            ResolvePrincipal = _ => Task.FromResult<ClaimsPrincipal?>(null),
        });

        try
        {
            var exchange = await InvokeThroughAsync(new SignalRComponent { ServerManager = manager }, port, null);

            ExchangePrincipal.Get(exchange).Should().BeNull(
                "SignalR's empty default user identifies nobody and must not show up as an identity");
        }
        finally
        {
            foreach (var c in _connections) await c.DisposeAsync();
            _connections.Clear();
            await _consumer!.Stop();
            _consumer = null;
        }
    }

    [Fact]
    public async Task ComponentAuthenticate_TakesPrecedenceOverTheHostResolver()
    {
        var port = GetFreePort();
        await using var manager = new SharedHttpServerManager(new HttpHostingOptions
        {
            ResolvePrincipal = _ => Task.FromResult<ClaimsPrincipal?>(User("host-user")),
        });

        try
        {
            var component = new SignalRComponent { ServerManager = manager, Authenticate = TokenAuth("good") };
            var exchange = await InvokeThroughAsync(component, port, "good");

            ExchangePrincipal.Get(exchange)!.FindFirst(ClaimTypes.NameIdentifier)!.Value.Should().Be("user-42",
                "the transport's own hook is the more specific decision on its path");
        }
        finally
        {
            foreach (var c in _connections) await c.DisposeAsync();
            _connections.Clear();
            await _consumer!.Stop();
            _consumer = null;
        }
    }

    // ── Волна 4: MessagePack really negotiates ──

    [Fact]
    public async Task MessagePack_ClientAndHubExchangeMessages()
    {
        var port = GetFreePort();
        var component = new SignalRComponent { ServerManager = _manager };
        var endpoint = CreateEndpoint(component, port, "/mp", "inOut=true&messagePack=true");
        await endpoint.Start();
        _consumer = (SignalRConsumer)endpoint.CreateConsumer(new EchoProcessor());
        await _consumer.Start();

        var connection = await ConnectAsync(port, "/mp", b => b.AddMessagePackProtocol());
        var reply = await connection.InvokeAsync<object?>("Invoke", "Send", new object?[] { "binary" });

        reply!.ToString().Should().Contain("binary",
            "messagePack=true обязан включать протокол MessagePack, а не JSON под его именем");
    }

    // ── Волна 5: statistics ──

    [Fact]
    public async Task Consumer_RecordsEndpointStatistics()
    {
        var port = GetFreePort();
        var component = new SignalRComponent { ServerManager = _manager };
        var endpoint = CreateEndpoint(component, port, "/stats", "inOut=true");
        await endpoint.Start();
        _consumer = (SignalRConsumer)endpoint.CreateConsumer(new EchoProcessor());
        await _consumer.Start();

        var connection = await ConnectAsync(port, "/stats");
        await connection.InvokeAsync<object?>("Invoke", "Send", new object?[] { "one" });

        // Ownership audit: MessagesIn is recorded by the core's StatisticsProcessor around a
        // ROUTED consumer - self-recording here double-counted it. A hand-built consumer stays
        // at zero; the routed contract lives in SignalRStatisticsOwnershipTests.
        var stats = (IEndpointStatistics)endpoint;
        stats.MessagesIn.Should().Be(0, "MessagesIn пишет ядро вокруг маршрутного консьюмера");
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

    private sealed class CapturingProcessor : IProcessor
    {
        public IExchange? LastExchange { get; private set; }

        public Task Process(IExchange exchange, CancellationToken ct = default)
        {
            LastExchange = exchange.Snapshot();
            exchange.Out = new Message("ok");
            return Task.CompletedTask;
        }
    }
}

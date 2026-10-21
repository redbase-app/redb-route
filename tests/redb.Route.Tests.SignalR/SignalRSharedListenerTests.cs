using System.Collections.Concurrent;
using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Http;
using redb.Route.SignalR;

namespace redb.Route.Tests.SignalR;

/// <summary>
/// A hub on a listener it shares with other route contexts, as on a Tsak worker: every hub has a container of its
/// own and joins or leaves the running listener without touching anything else on it. The S1 to S5 scenarios are
/// those of the Honest report of 2026-10-21 (doc/route/REPORT-2026-10-21-signalr-shared-listener.md): one
/// <see cref="SharedHttpServerManager"/> for all contexts, an HTTP context next to the hub context, the hub context
/// with or without a broadcast endpoint (<c>mode=server</c>), and a restart that disposes the hub context and starts a
/// new one with the same routes.
/// </summary>
public sealed class SignalRSharedListenerTests : IAsyncLifetime
{
    private readonly SharedHttpServerManager _shared = new();
    private readonly ErrorLog _log = new();
    private readonly ILoggerFactory _loggers;
    private readonly HttpClient _http = new();
    private readonly List<RouteContext> _contexts = [];
    private readonly List<HubConnection> _connections = [];
    private readonly List<IConsumer> _consumers = [];
    private readonly List<IProducer> _producers = [];

    public SignalRSharedListenerTests()
        => _loggers = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Warning).AddProvider(_log));

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var c in _connections) await c.DisposeAsync();
        foreach (var p in _producers) await p.Stop();
        foreach (var c in _consumers) await c.Stop();
        foreach (var c in _contexts) await c.DisposeAsync();
        await _shared.DisposeAsync();
        _http.Dispose();
        _loggers.Dispose();
    }

    private static int GetFreePort() => global::redb.Route.Tests.Shared.TestPorts.Next();

    // ── Contexts as the report builds them ──

    /// <summary>The "omni" context: one HTTP route on the shared listener.</summary>
    private async Task<RouteContext> StartHttpContextAsync(int port)
    {
        var context = new RouteContext("probe.omni", _loggers);
        context.AddComponent(new HttpComponent { ServerManager = _shared });
        context.AddRoutes(r => r.From($"http://127.0.0.1:{port}/ping?inOut=true").Process(e => e.In.Body = "pong"));
        await context.Start();
        _contexts.Add(context);
        return context;
    }

    /// <summary>The "api" context: the hub, answering with its tag, and optionally the broadcast into it.</summary>
    private async Task<RouteContext> StartHubContextAsync(int port, bool broadcast, string tag = "api",
        string hubPath = "/hub/probe")
    {
        var context = new RouteContext($"probe.{tag}", _loggers);
        context.AddComponent(new SignalRComponent { ServerManager = _shared });
        var hub = $"signalr://127.0.0.1:{port}{hubPath}";
        context.AddRoutes(r => r.From(hub + "?inOut=true").Process(e => e.Out = new Message($"{tag}:{e.In.Body}")));
        if (broadcast)
            context.AddRoutes(r => r.From($"direct://{tag}-broadcast").To(hub + "?mode=server&method=uiChanged"));
        await context.Start();
        _contexts.Add(context);
        return context;
    }

    private async Task RestartAsync(RouteContext context)
    {
        _contexts.Remove(context);
        await context.DisposeAsync();
    }

    private static async Task BroadcastAsync(RouteContext context, string tag, string body)
    {
        using var template = new ProducerTemplate(context);
        template.Start();
        await template.SendAsync($"direct://{tag}-broadcast", body);
    }

    private async Task<HttpStatusCode> NegotiateAsync(int port, string hubPath = "/hub/probe")
    {
        using var response = await _http.PostAsync(
            $"http://127.0.0.1:{port}{hubPath}/negotiate?negotiateVersion=1", null);
        return response.StatusCode;
    }

    private async Task<HubConnection> ConnectAsync(int port, string hubPath = "/hub/probe",
        HttpTransportType? transport = null, string? accessToken = null, Action<IHubConnectionBuilder>? configure = null)
    {
        var builder = new HubConnectionBuilder().WithUrl($"http://127.0.0.1:{port}{hubPath}", o =>
        {
            if (transport is { } only) o.Transports = only;
            if (accessToken is not null) o.AccessTokenProvider = () => Task.FromResult<string?>(accessToken);
        });
        configure?.Invoke(builder);
        var connection = builder.Build();
        _connections.Add(connection);
        await connection.StartAsync();
        return connection;
    }

    private static async Task<string?> InvokeAsync(HubConnection connection, string body = "x")
        => (await connection.InvokeAsync<object?>("Invoke", "Send", new object?[] { body }))?.ToString();

    private static TaskCompletionSource<string> Receive(HubConnection connection, string method = "uiChanged")
    {
        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.On<string>(method, m => received.TrySetResult(m));
        return received;
    }

    /// <summary>The context started every route, and nothing logged an error (DEGRADED is an error).</summary>
    private void ShouldBeHealthy(params RouteContext[] contexts)
    {
        foreach (var context in contexts)
            context.Routes.Should().OnlyContain(r => r.Status == RouteStatus.Started,
                $"контекст {context.ContextId} обязан запустить все маршруты, без DEGRADED");
        _log.Errors.Should().BeEmpty("на общем слушателе хаб не должен давать ошибок старта");
    }

    // ── S1–S5 ──

    [Fact]
    public async Task S1_HubJoinsAListenerAnotherContextAlreadyServes()
    {
        var port = GetFreePort();
        var omni = await StartHttpContextAsync(port);
        var api = await StartHubContextAsync(port, broadcast: true);

        ShouldBeHealthy(omni, api);
        (await NegotiateAsync(port)).Should().Be(HttpStatusCode.OK);
        (await _http.GetStringAsync($"http://127.0.0.1:{port}/ping")).Should().Be("pong");

        var client = await ConnectAsync(port);
        (await InvokeAsync(client)).Should().Be("api:x");
        var received = Receive(client);
        await BroadcastAsync(api, "api", "changed");
        (await received.Task.WaitAsync(TimeSpan.FromSeconds(10))).Should().Be("changed");
    }

    [Fact]
    public async Task S2_HubFirstThenHttp_RestartingTheHubContextKeepsServing()
    {
        var port = GetFreePort();
        var api = await StartHubContextAsync(port, broadcast: true);
        var omni = await StartHttpContextAsync(port);
        (await NegotiateAsync(port)).Should().Be(HttpStatusCode.OK, "точка рассылки не регистрирует хаб второй раз");

        await RestartAsync(api);
        var restarted = await StartHubContextAsync(port, broadcast: true, tag: "api2");

        ShouldBeHealthy(omni, restarted);
        (await NegotiateAsync(port)).Should().Be(HttpStatusCode.OK);
        (await _http.GetStringAsync($"http://127.0.0.1:{port}/ping")).Should().Be("pong");

        var client = await ConnectAsync(port);
        (await InvokeAsync(client)).Should().Be("api2:x");
        var received = Receive(client);
        await BroadcastAsync(restarted, "api2", "changed");
        (await received.Task.WaitAsync(TimeSpan.FromSeconds(10))).Should().Be("changed");
    }

    [Fact]
    public async Task S3_HubWithBroadcastOnItsOwnPort_ListenerStopsAndComesBack()
    {
        var port = GetFreePort();
        var api = await StartHubContextAsync(port, broadcast: true);
        (await NegotiateAsync(port)).Should().Be(HttpStatusCode.OK);

        await RestartAsync(api);
        _shared.ServerCount.Should().Be(0, "без маршрутов и хабов слушатель останавливается");

        var restarted = await StartHubContextAsync(port, broadcast: true, tag: "api2");
        ShouldBeHealthy(restarted);
        (await NegotiateAsync(port)).Should().Be(HttpStatusCode.OK);

        var client = await ConnectAsync(port);
        var received = Receive(client);
        await BroadcastAsync(restarted, "api2", "changed");
        (await received.Task.WaitAsync(TimeSpan.FromSeconds(10))).Should().Be("changed");
    }

    [Fact]
    public async Task S4_HubWithoutBroadcastOnItsOwnPort_RestartsCleanly()
    {
        var port = GetFreePort();
        var api = await StartHubContextAsync(port, broadcast: false);
        (await NegotiateAsync(port)).Should().Be(HttpStatusCode.OK);

        await RestartAsync(api);
        var restarted = await StartHubContextAsync(port, broadcast: false, tag: "api2");

        ShouldBeHealthy(restarted);
        (await NegotiateAsync(port)).Should().Be(HttpStatusCode.OK);
        (await InvokeAsync(await ConnectAsync(port))).Should().Be("api2:x");
    }

    [Fact]
    public async Task S5_AfterARestartTheNewContextAnswers_NotTheOldMapping()
    {
        var port = GetFreePort();
        var api = await StartHubContextAsync(port, broadcast: false);
        var omni = await StartHttpContextAsync(port);
        (await InvokeAsync(await ConnectAsync(port))).Should().Be("api:x");

        await RestartAsync(api);
        var restarted = await StartHubContextAsync(port, broadcast: false, tag: "api2");

        ShouldBeHealthy(omni, restarted);
        (await InvokeAsync(await ConnectAsync(port))).Should().Be("api2:x",
            "после перезапуска отвечает новый приёмник, а не разметка прежнего экземпляра");
    }

    // ── Hubs of two contexts on one listener ──

    [Fact]
    public async Task RemovingOneContextsHub_LeavesTheOtherContextsConnectionsServing()
    {
        var port = GetFreePort();
        var alpha = await StartHubContextAsync(port, broadcast: true, tag: "alpha", hubPath: "/alpha");
        var beta = await StartHubContextAsync(port, broadcast: true, tag: "beta", hubPath: "/beta");

        var toAlpha = await ConnectAsync(port, "/alpha");
        var toBeta = await ConnectAsync(port, "/beta");
        var alphaClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        toAlpha.Closed += _ => { alphaClosed.TrySetResult(); return Task.CompletedTask; };

        await RestartAsync(alpha);

        await alphaClosed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        (await NegotiateAsync(port, "/alpha")).Should().Be(HttpStatusCode.NotFound, "снятый хаб уходит со слушателя");

        toBeta.State.Should().Be(HubConnectionState.Connected, "снятие чужого хаба не рвёт соединения этого");
        (await InvokeAsync(toBeta)).Should().Be("beta:x");
        var received = Receive(toBeta);
        await BroadcastAsync(beta, "beta", "still here");
        (await received.Task.WaitAsync(TimeSpan.FromSeconds(10))).Should().Be("still here");
        ShouldBeHealthy(beta);
    }

    // ── Isolation of the hubs of one listener ──

    private static Func<HttpContext, Task<ClaimsPrincipal?>> UserFromToken => ctx =>
    {
        var token = ctx.Request.Query["access_token"].ToString();
        if (string.IsNullOrEmpty(token))
        {
            var header = ctx.Request.Headers.Authorization.ToString();
            token = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header["Bearer ".Length..] : "";
        }

        return Task.FromResult<ClaimsPrincipal?>(string.IsNullOrEmpty(token)
            ? null
            : new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, token)], "test")));
    };

    /// <summary>Joins the group "g" on the method "Join", answers anything else.</summary>
    private sealed class GroupProcessor : IProcessor
    {
        public Task Process(IExchange exchange, CancellationToken ct = default)
        {
            if (exchange.In.GetHeader<string>(SignalRHeaders.Method) == "Join")
                exchange.In.Headers[SignalRHeaders.AddToGroup] = "g";
            return Task.CompletedTask;
        }
    }

    private async Task<SignalREndpoint> StartHubAsync(SignalRComponent component, int port, string hubPath,
        string? extra = null)
    {
        var endpoint = (SignalREndpoint)component.CreateEndpoint(
            EndpointUriParser.Parse($"signalr://127.0.0.1:{port}{hubPath}" + (extra is null ? "" : "?" + extra)));
        await endpoint.Start();
        var consumer = endpoint.CreateConsumer(new GroupProcessor());
        _consumers.Add(consumer);
        await consumer.Start();
        return endpoint;
    }

    private async Task PushAsync(SignalRComponent component, int port, string hubPath, Action<IMessage> target)
    {
        var endpoint = (SignalREndpoint)component.CreateEndpoint(
            EndpointUriParser.Parse($"signalr://127.0.0.1:{port}{hubPath}?mode=server&method=note"));
        var producer = endpoint.CreateProducer();
        _producers.Add(producer);
        await producer.Start();
        var message = new Message("from " + hubPath);
        target(message);
        await producer.Process(new Exchange(message));
    }

    [Fact]
    public async Task AllGroupAndUser_StayOnTheirOwnHubPath()
    {
        var port = GetFreePort();
        var component = new SignalRComponent { ServerManager = _shared, Authenticate = UserFromToken };
        await StartHubAsync(component, port, "/left");
        await StartHubAsync(component, port, "/right");

        // The same user in the same group on both hubs: only the path tells them apart.
        var left = await ConnectAsync(port, "/left", accessToken: "u1");
        var right = await ConnectAsync(port, "/right", accessToken: "u1");
        var leftGot = new ConcurrentQueue<string>();
        var rightGot = new ConcurrentQueue<string>();
        left.On<string>("note", leftGot.Enqueue);
        right.On<string>("note", rightGot.Enqueue);
        await left.InvokeAsync<object?>("Invoke", "Join", Array.Empty<object?>());
        await right.InvokeAsync<object?>("Invoke", "Join", Array.Empty<object?>());

        await PushAsync(component, port, "/right", _ => { });
        await PushAsync(component, port, "/right", m =>
        {
            m.Headers[SignalRHeaders.Target] = "Group";
            m.Headers[SignalRHeaders.Group] = "g";
        });
        await PushAsync(component, port, "/right", m =>
        {
            m.Headers[SignalRHeaders.Target] = "User";
            m.Headers[SignalRHeaders.TargetUser] = "u1";
        });

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (rightGot.Count < 3 && DateTime.UtcNow < deadline)
            await Task.Delay(50);
        await Task.Delay(300);

        rightGot.Should().HaveCount(3, "All, Group и User доходят до клиентов своего хаба");
        leftGot.Should().BeEmpty("рассылка одного пути хаба не доходит до клиентов другого пути того же слушателя");
    }

    // ── Protocols and transports are per hub ──

    [Fact]
    public async Task MessagePack_IsAPropertyOfOneHub()
    {
        var port = GetFreePort();
        var component = new SignalRComponent { ServerManager = _shared };
        await StartHubAsync(component, port, "/packed", "messagePack=true&inOut=true");
        await StartHubAsync(component, port, "/plain", "inOut=true");

        var packed = await ConnectAsync(port, "/packed", configure: b => b.AddMessagePackProtocol());
        packed.State.Should().Be(HubConnectionState.Connected);

        var act = () => ConnectAsync(port, "/plain", configure: b => b.AddMessagePackProtocol());
        await act.Should().ThrowAsync<Exception>("MessagePack включён только на своём хабе");
        (await ConnectAsync(port, "/plain")).State.Should().Be(HubConnectionState.Connected);
    }

    [Theory]
    [InlineData(HttpTransportType.WebSockets)]
    [InlineData(HttpTransportType.ServerSentEvents)]
    [InlineData(HttpTransportType.LongPolling)]
    public async Task EveryTransport_ReachesAHubMappedOntoARunningListener(HttpTransportType transport)
    {
        var port = GetFreePort();
        var omni = await StartHttpContextAsync(port);
        var component = new SignalRComponent { ServerManager = _shared };
        var endpoint = (SignalREndpoint)component.CreateEndpoint(
            EndpointUriParser.Parse($"signalr://127.0.0.1:{port}/late?inOut=true"));
        await endpoint.Start();
        var consumer = endpoint.CreateConsumer(new EchoProcessor());
        _consumers.Add(consumer);
        await consumer.Start();

        var client = await ConnectAsync(port, "/late", transport);
        (await InvokeAsync(client, "ping")).Should().Contain("ping");
        ShouldBeHealthy(omni);
    }

    [Fact]
    public async Task A_TransportRestrictedHub_RefusesTheOthers()
    {
        var port = GetFreePort();
        var component = new SignalRComponent { ServerManager = _shared };
        await StartHubAsync(component, port, "/polling", "transport=LongPolling&inOut=true");
        await StartHubAsync(component, port, "/any", "inOut=true");

        var act = () => ConnectAsync(port, "/polling", HttpTransportType.WebSockets);
        await act.Should().ThrowAsync<Exception>("транспорт задаётся на хабе, а не на слушателе");
        (await ConnectAsync(port, "/polling", HttpTransportType.LongPolling)).State.Should().Be(HubConnectionState.Connected);
        (await ConnectAsync(port, "/any", HttpTransportType.WebSockets)).State.Should().Be(HubConnectionState.Connected);
    }

    // ── A literal hub path against the catch-all of the route table ──

    [Fact]
    public async Task HubPath_WinsOverTheRouteTable_AndGivesItBackWhenRemoved()
    {
        var port = GetFreePort();
        _shared.RegisterRoute("127.0.0.1", port, "/hub/probe/negotiate", "POST",
            ctx => ctx.Response.WriteAsync("route-table"));
        _shared.RegisterRoute("127.0.0.1", port, "/hub/other", "GET", ctx => ctx.Response.WriteAsync("other"));
        await _shared.EnsureStarted("127.0.0.1", port);

        var api = await StartHubContextAsync(port, broadcast: false);
        using (var negotiate = await _http.PostAsync($"http://127.0.0.1:{port}/hub/probe/negotiate?negotiateVersion=1", null))
            (await negotiate.Content.ReadAsStringAsync()).Should().Contain("connectionId", "путь хаба обслуживает хаб");
        (await _http.GetStringAsync($"http://127.0.0.1:{port}/hub/other")).Should().Be("other");

        await RestartAsync(api);
        using (var negotiate = await _http.PostAsync($"http://127.0.0.1:{port}/hub/probe/negotiate?negotiateVersion=1", null))
            (await negotiate.Content.ReadAsStringAsync()).Should().Be("route-table", "без хаба путь снова у таблицы маршрутов");
    }

    private sealed class EchoProcessor : IProcessor
    {
        public Task Process(IExchange exchange, CancellationToken ct = default)
        {
            exchange.Out = new Message($"echo:{exchange.In.Body}");
            return Task.CompletedTask;
        }
    }

    /// <summary>Errors logged by the contexts and the hubs.</summary>
    private sealed class ErrorLog : ILoggerProvider
    {
        public ConcurrentQueue<string> Errors { get; } = new();

        public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

        public void Dispose() { }

        private sealed class Logger(ErrorLog log, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (IsEnabled(logLevel))
                    log.Errors.Enqueue($"{category}: {formatter(state, exception)} {exception}");
            }
        }
    }
}

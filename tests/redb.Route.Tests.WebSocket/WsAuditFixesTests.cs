using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.AspNetCore.Http;
using OpenTelemetry;
using OpenTelemetry.Trace;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Telemetry;
using redb.Route.WebSocket;

namespace redb.Route.Tests.WebSocket;

/// <summary>
/// Ф14 волны 2, 5 и 6: the findings of the WebSocket audit, one test each.
/// Every test here was red before its fix.
/// </summary>
public sealed class WsAuditFixesTests : IAsyncLifetime
{
    private readonly List<IConsumer> _consumers = [];
    private readonly List<IProducer> _producers = [];
    private readonly List<string> _tempFiles = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var p in _producers) await p.Stop();
        foreach (var c in _consumers) await c.Stop();
        foreach (var f in _tempFiles) if (File.Exists(f)) File.Delete(f);
    }

    private static int GetFreePort()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private static WsEndpoint Endpoint(WsComponent component, string path,
        Dictionary<string, string>? pars = null)
    {
        var raw = $"{component.Scheme}:{path}";
        var uri = new EndpointUri(component.Scheme, "/" + path, raw, pars ?? []);
        return (WsEndpoint)component.CreateEndpoint(uri);
    }

    private WsConsumer StartConsumer(WsEndpoint endpoint, IProcessor processor)
    {
        var consumer = (WsConsumer)endpoint.CreateConsumer(processor);
        _consumers.Add(consumer);
        return consumer;
    }

    // ── WS-2. wss:// without a certificate used to open a PLAINTEXT socket ──

    [Fact]
    public async Task WssConsumer_WithoutCertificate_FailsLoud()
    {
        var port = GetFreePort();
        var component = new WssComponent();
        var endpoint = Endpoint(component, $"127.0.0.1:{port}/secure");
        var consumer = StartConsumer(endpoint, Substitute.For<IProcessor>());

        var act = () => consumer.Start();

        (await act.Should().ThrowAsync<InvalidOperationException>(
            "wss без сертификата поднимал открытый сокет и рапортовал wss:// в логе"))
            .WithMessage("*sslCertPath*");
    }

    // ── Волна 2. The handshake is authenticated by a host-supplied delegate ──

    [Fact]
    public async Task Authenticate_RejectsHandshakeWithoutToken()
    {
        var port = GetFreePort();
        var component = new WsComponent { Authenticate = TokenAuth("good") };
        var endpoint = Endpoint(component, $"127.0.0.1:{port}/secure");
        var consumer = StartConsumer(endpoint, Substitute.For<IProcessor>());
        await consumer.Start();

        using var client = new ClientWebSocket();
        var act = () => client.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/secure"), CancellationToken.None);

        await act.Should().ThrowAsync<WebSocketException>("апгрейд без токена обязан отклоняться");
        consumer.ActiveConnections.Should().Be(0);
    }

    [Fact]
    public async Task Authenticate_PutsUserIdOnTheExchange()
    {
        var port = GetFreePort();
        var component = new WsComponent { Authenticate = TokenAuth("good") };
        var endpoint = Endpoint(component, $"127.0.0.1:{port}/secure");
        var captured = new CapturingProcessor();
        var consumer = StartConsumer(endpoint, captured);
        await consumer.Start();

        using var client = new ClientWebSocket();
        await client.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/secure?access_token=good"), CancellationToken.None);
        await client.SendAsync(Encoding.UTF8.GetBytes("hi"), WebSocketMessageType.Text, true, CancellationToken.None);

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (captured.LastExchange is null && DateTime.UtcNow < deadline)
            await Task.Delay(20);

        captured.LastExchange.Should().NotBeNull();
        captured.LastExchange!.In.GetHeader<string>(WsHeaders.UserId).Should().Be("user-42");
    }

    // ── WS-5. localhost in a consumer URI used to be a FormatException ──

    [Fact]
    public async Task Consumer_OnLocalhost_Starts()
    {
        var port = GetFreePort();
        var component = new WsComponent();
        var endpoint = Endpoint(component, $"localhost:{port}/chat");
        var consumer = StartConsumer(endpoint, Substitute.For<IProcessor>());

        await consumer.Start();

        using var client = new ClientWebSocket();
        await client.ConnectAsync(new Uri($"ws://localhost:{port}/chat"), CancellationToken.None);
        client.State.Should().Be(WebSocketState.Open,
            "IPAddress.Parse(\"localhost\") бросал FormatException вместо запуска слушателя");
    }

    // ── WS-4. The server could not push to its own clients ──

    [Fact]
    public async Task ServerModeProducer_BroadcastsToAllClients()
    {
        var port = GetFreePort();
        var component = new WsComponent();
        var endpoint = Endpoint(component, $"127.0.0.1:{port}/feed");
        var consumer = StartConsumer(endpoint, Substitute.For<IProcessor>());
        await consumer.Start();

        using var first = new ClientWebSocket();
        using var second = new ClientWebSocket();
        await first.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/feed"), CancellationToken.None);
        await second.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/feed"), CancellationToken.None);
        await WaitForConnections(consumer, 2);

        var pushEndpoint = Endpoint(component, $"127.0.0.1:{port}/feed",
            new Dictionary<string, string> { ["mode"] = "Server" });
        var producer = pushEndpoint.CreateProducer();
        _producers.Add(producer);
        await producer.Start();

        await producer.Process(new Exchange(new Message("quote:42")));

        (await ReceiveText(first)).Should().Be("quote:42");
        (await ReceiveText(second)).Should().Be("quote:42",
            "маршрут обязан уметь инициативно слать своим ws-клиентам, а не только отвечать");
    }

    [Fact]
    public async Task ServerModeProducer_SendsToOneConnection()
    {
        var port = GetFreePort();
        var component = new WsComponent();
        var endpoint = Endpoint(component, $"127.0.0.1:{port}/feed");
        var consumer = StartConsumer(endpoint, Substitute.For<IProcessor>());
        await consumer.Start();

        using var target = new ClientWebSocket();
        using var other = new ClientWebSocket();
        await target.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/feed"), CancellationToken.None);
        await WaitForConnections(consumer, 1);
        var targetId = consumer.ConnectionIds.Single();

        await other.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/feed"), CancellationToken.None);
        await WaitForConnections(consumer, 2);

        var pushEndpoint = Endpoint(component, $"127.0.0.1:{port}/feed",
            new Dictionary<string, string> { ["mode"] = "Server" });
        var producer = pushEndpoint.CreateProducer();
        _producers.Add(producer);
        await producer.Start();

        var exchange = new Exchange(new Message("private"));
        exchange.In.Headers[WsHeaders.TargetConnection] = targetId;
        await producer.Process(exchange);

        (await ReceiveText(target)).Should().Be("private");

        var stray = ReceiveText(other);
        var raced = await Task.WhenAny(stray, Task.Delay(300));
        raced.Should().NotBeSameAs(stray, "адресный push не должен уходить остальным клиентам");
    }

    [Fact]
    public async Task ServerModeProducer_WithoutConsumer_FailsLoud()
    {
        var port = GetFreePort();
        var component = new WsComponent();
        var pushEndpoint = Endpoint(component, $"127.0.0.1:{port}/nobody",
            new Dictionary<string, string> { ["mode"] = "Server" });
        var producer = pushEndpoint.CreateProducer();
        _producers.Add(producer);

        var act = () => producer.Start();
        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*consumer*");
    }

    // ── WS-6. wss:// producer could not talk to a self-signed staging server ──

    [Fact]
    public async Task Producer_TrustAllCertificates_ConnectsToSelfSignedServer()
    {
        var port = GetFreePort();
        var certPath = WriteSelfSignedPfx("localhost");

        var serverComponent = new WssComponent();
        var serverEndpoint = Endpoint(serverComponent, $"127.0.0.1:{port}/secure",
            new Dictionary<string, string>
            {
                ["sslCertPath"] = certPath,
                ["inOut"] = "true",
            });
        var consumer = StartConsumer(serverEndpoint, new EchoProcessor());
        await consumer.Start();

        var clientComponent = new WssComponent();
        var clientEndpoint = Endpoint(clientComponent, $"localhost:{port}/secure",
            new Dictionary<string, string>
            {
                ["trustAllCertificates"] = "true",
                ["inOut"] = "true",
            });
        var producer = clientEndpoint.CreateProducer();
        _producers.Add(producer);
        await producer.Start();

        var exchange = new Exchange(new Message("tls"));
        await producer.Process(exchange);

        exchange.Out!.Body.Should().Be("echo:tls",
            "у ClientWebSocket не настраивалась проверка сертификата вовсе: staging был недостижим");
    }

    // ── WS-7. reconnect=true against a dead server never returned ──

    [Fact]
    public async Task Producer_ReconnectTimeout_FailsInsteadOfHangingForever()
    {
        var port = GetFreePort();
        var component = new WsComponent();
        var serverEndpoint = Endpoint(component, $"127.0.0.1:{port}/dead");
        var consumer = StartConsumer(serverEndpoint, Substitute.For<IProcessor>());
        await consumer.Start();

        var clientEndpoint = Endpoint(component, $"127.0.0.1:{port}/dead",
            new Dictionary<string, string>
            {
                ["reconnect"] = "true",
                ["reconnectInterval"] = "50",
                ["reconnectTimeout"] = "300",
                ["connectTimeout"] = "200",
            });
        var producer = clientEndpoint.CreateProducer();
        _producers.Add(producer);
        await producer.Start();
        await producer.Process(new Exchange(new Message("while it is up")));

        await consumer.Stop(); // the listener goes away under the producer

        var started = Stopwatch.StartNew();
        var act = async () =>
        {
            // The first send after the shutdown may still succeed into a socket that has not yet
            // noticed the close; the one after it enters the reconnect loop, where the hang was.
            for (var i = 0; i < 10; i++)
                await producer.Process(new Exchange(new Message("after it is gone")));
        };
        await act.Should().ThrowAsync<Exception>(
            "с reconnect=true и maxReconnectAttempts=0 обмен не возвращался никогда: " +
            "ошибка не всплывала, dead-letter не срабатывал, маршрут просто висел");
        started.Stop();

        started.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(15),
            "reconnectTimeout — это потолок, а не пожелание");
    }

    // ── WS-8. the producer name leaked the URI password into the log ──

    [Fact]
    public void ProducerName_MasksPassword()
    {
        var component = new WsComponent();
        var endpoint = Endpoint(component, "user:s3cr3t@example.com:9000/feed");
        var producer = (WsProducer)endpoint.CreateProducer();

        producer.DiagnosticName.Should().NotContain("s3cr3t",
            "имя продюсера уезжает в лог строкой «producer started»");
    }

    // ── WS-12. the producer span had no destination ──

    [Fact]
    public async Task ProducerSpan_CarriesDestination()
    {
        var port = GetFreePort();
        var component = new WsComponent();
        var endpoint = Endpoint(component, $"127.0.0.1:{port}/feed");
        var consumer = StartConsumer(endpoint, Substitute.For<IProcessor>());
        await consumer.Start();

        var activities = new List<Activity>();
        using var tracer = Sdk.CreateTracerProviderBuilder()
            .AddSource(RouteActivitySource.SourceName)
            .AddInMemoryExporter(activities)
            .Build()!;

        var producer = endpoint.CreateProducer();
        _producers.Add(producer);
        await producer.Start();
        await producer.Process(new Exchange(new Message("x")));

        tracer.ForceFlush(1000);

        // The listener is process-wide (see WsTelemetrySmokeTests): select this producer's span by kind
        // and by its own port, not by position.
        var span = activities.Should().ContainSingle(a =>
            a.Kind == ActivityKind.Producer
            && ((a.GetTagItem("redb.route.endpoint") as string) ?? string.Empty).Contains($":{port}/")).Subject;
        span.GetTagItem("messaging.destination.name").Should().Be("/feed",
            "у SignalR destination был, у WebSocket — нет, хотя адрес известен");
    }

    // ── WS-1. producer statistics were never recorded ──

    [Fact]
    public async Task Producer_RecordsEndpointStatistics()
    {
        var port = GetFreePort();
        var component = new WsComponent();
        var endpoint = Endpoint(component, $"127.0.0.1:{port}/stats");
        var consumer = StartConsumer(endpoint, Substitute.For<IProcessor>());
        await consumer.Start();

        var producer = endpoint.CreateProducer();
        _producers.Add(producer);
        await producer.Start();
        await producer.Process(new Exchange(new Message("hello")));

        // Ownership audit: MessagesOut belongs to the core (ToProcessor / the template), so a
        // hand-built producer records only what the core cannot see - the wire bytes. The routed
        // counterpart lives in WsStatisticsOwnershipTests.
        var stats = (IEndpointStatistics)endpoint;
        stats.BytesOut.Should().Be(5, "wire-байты остаются за коннектором");
        stats.MessagesOut.Should().Be(0, "MessagesOut пишет ядро, самозапись задваивала в маршруте");
    }

    // ── The DSL exposes what the options gained ──

    [Fact]
    public void Dsl_BroadcastAndNewOptions()
    {
        Ws.Broadcast("0.0.0.0:8080/stream").Build()
            .Should().Be("ws:0.0.0.0:8080/stream?mode=Server");

        Ws.Connect("staging:8443/feed").Ssl().TrustAllCertificates().Build()
            .Should().Be("wss:staging:8443/feed?ssl=true&trustAllCertificates=true");

        Ws.Connect("host:8080/feed").Reconnect(intervalMs: 1000).ReconnectTimeout(30_000).Build()
            .Should().Contain("reconnectTimeout=30000");
    }

    // ── Мелочи: a bad encoding name threw a bare framework ArgumentException ──

    [Fact]
    public void UnknownEncoding_IsRejectedByValidate()
    {
        var component = new WsComponent();

        var act = () => Endpoint(component, "127.0.0.1:9000/x",
            new Dictionary<string, string> { ["encoding"] = "utf-9" });

        act.Should().Throw<ArgumentException>().WithMessage("*utf-9*");
    }

    // ── Helpers ──

    private static async Task WaitForConnections(WsConsumer consumer, int expected)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (consumer.ActiveConnections < expected && DateTime.UtcNow < deadline)
            await Task.Delay(20);
        consumer.ActiveConnections.Should().BeGreaterThanOrEqualTo(expected);
    }

    private static async Task<string> ReceiveText(ClientWebSocket client)
    {
        var buffer = new byte[8192];
        var result = await client.ReceiveAsync(buffer, CancellationToken.None);
        return Encoding.UTF8.GetString(buffer, 0, result.Count);
    }

    private string WriteSelfSignedPfx(string cn)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest($"CN={cn}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));

        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        san.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(san.Build());

        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        var path = Path.Combine(Path.GetTempPath(), $"redb-ws-{Guid.NewGuid():N}.pfx");
        File.WriteAllBytes(path, cert.Export(X509ContentType.Pfx));
        _tempFiles.Add(path);
        return path;
    }

    /// <summary>
    /// What a host is expected to write: browsers cannot set headers on a WebSocket handshake, so
    /// the token comes in the query string, and a non-browser client may still use the header.
    /// </summary>
    private static Func<HttpContext, Task<System.Security.Claims.ClaimsPrincipal?>> TokenAuth(string expected) => ctx =>
    {
        var header = ctx.Request.Headers.Authorization.ToString();
        var token = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? header["Bearer ".Length..]
            : ctx.Request.Query["access_token"].ToString();

        return Task.FromResult<System.Security.Claims.ClaimsPrincipal?>(token == expected
            ? new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
                [new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.NameIdentifier, "user-42")], "test"))
            : null);
    };

    private sealed class EchoProcessor : IProcessor
    {
        public Task Process(IExchange exchange, CancellationToken ct = default)
        {
            exchange.Out = new Message("echo:" + exchange.In.Body);
            return Task.CompletedTask;
        }
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

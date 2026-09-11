using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.SignalR.Client;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Http;
using redb.Route.SignalR;

namespace redb.Route.Tests.SignalR;

/// <summary>
/// Ф14 волны 2, 5 и 6: the findings of the SignalR audit, one test each.
/// Every test here was red before its fix.
/// </summary>
public sealed class SignalRAuditFixesTests : IAsyncLifetime
{
    private readonly SharedHttpServerManager _manager = new();
    private readonly List<IConsumer> _consumers = [];
    private readonly List<IProducer> _producers = [];
    private readonly List<IAsyncDisposable> _connections = [];
    private readonly List<string> _tempFiles = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var c in _connections) await c.DisposeAsync();
        foreach (var p in _producers) await p.Stop();
        foreach (var c in _consumers) await c.Stop();
        await _manager.DisposeAsync();
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

    private static SignalREndpoint Endpoint(SignalRComponent component, string path, string? pars = null)
    {
        var uri = EndpointUriParser.Parse($"signalr://{path}" + (pars is null ? "" : $"?{pars}"));
        return (SignalREndpoint)component.CreateEndpoint(uri);
    }

    // ── SR-3. ssl=true without a certificate used to open a PLAINTEXT listener ──

    [Fact]
    public async Task Consumer_SslWithoutCertificate_FailsLoud()
    {
        var port = GetFreePort();
        var component = new SignalRComponent { ServerManager = _manager };
        var endpoint = Endpoint(component, $"127.0.0.1:{port}/secure", "ssl=true");
        var consumer = endpoint.CreateConsumer(Substitute.For<IProcessor>());
        _consumers.Add(consumer);

        var act = () => consumer.Start();

        (await act.Should().ThrowAsync<InvalidOperationException>(
            "ssl=true без сертификата поднимал открытый порт и рапортовал https:// в логе"))
            .WithMessage("*sslCertPath*");
    }

    // ── SR-4. Certificate validation used to switch off whenever ssl was NOT set ──

    [Fact]
    public async Task Producer_TrustAllCertificates_IsExplicitAndWorks()
    {
        var port = GetFreePort();
        var certPath = WriteSelfSignedPfx();

        var serverComponent = new SignalRComponent { ServerManager = _manager };
        var hub = Endpoint(serverComponent, $"127.0.0.1:{port}/secure",
            $"ssl=true&sslCertPath={Uri.EscapeDataString(certPath)}&inOut=true");
        await hub.Start();
        var consumer = hub.CreateConsumer(new EchoProcessor());
        _consumers.Add(consumer);
        await consumer.Start();

        var clientComponent = new SignalRComponent { ServerManager = _manager };
        var trusting = Endpoint(clientComponent, $"localhost:{port}/secure",
            "ssl=true&trustAllCertificates=true&bridge=true&inOut=true&method=Send");
        var producer = trusting.CreateProducer();
        _producers.Add(producer);
        await producer.Start();

        var exchange = new Exchange(new Message("tls"));
        await producer.Process(exchange);
        exchange.Out!.Body!.ToString().Should().Contain("tls");
    }

    [Fact]
    public async Task Producer_WithoutTrustAll_RefusesSelfSignedCertificate()
    {
        var port = GetFreePort();
        var certPath = WriteSelfSignedPfx();

        var serverComponent = new SignalRComponent { ServerManager = _manager };
        var hub = Endpoint(serverComponent, $"127.0.0.1:{port}/secure",
            $"ssl=true&sslCertPath={Uri.EscapeDataString(certPath)}");
        await hub.Start();
        var consumer = hub.CreateConsumer(new EchoProcessor());
        _consumers.Add(consumer);
        await consumer.Start();

        var clientComponent = new SignalRComponent { ServerManager = _manager };
        var strict = Endpoint(clientComponent, $"localhost:{port}/secure", "ssl=true&bridge=true&method=Send");
        var producer = strict.CreateProducer();
        _producers.Add(producer);

        var act = () => producer.Start();
        await act.Should().ThrowAsync<Exception>(
            "отключение проверки сертификата обязано быть явным решением, а не побочным эффектом другой опции");
    }

    // ── SR-6. Server mode depends on the consumer being started first ──

    [Fact]
    public async Task ServerModeProducer_WithoutConsumer_FailsLoud()
    {
        var port = GetFreePort();
        var component = new SignalRComponent { ServerManager = _manager };
        var endpoint = Endpoint(component, $"127.0.0.1:{port}/orphan", "mode=Server&method=Push");
        var producer = endpoint.CreateProducer();
        _producers.Add(producer);

        var act = () => producer.Start();
        (await act.Should().ThrowAsync<InvalidOperationException>(
            "зависимость порядка старта нигде не была зафиксирована тестом"))
            .WithMessage("*consumer*");
    }

    [Fact]
    public async Task ServerModeProducer_BroadcastsToHubClients()
    {
        var port = GetFreePort();
        var component = new SignalRComponent { ServerManager = _manager };
        var endpoint = Endpoint(component, $"127.0.0.1:{port}/feed");
        await endpoint.Start();
        var consumer = endpoint.CreateConsumer(Substitute.For<IProcessor>());
        _consumers.Add(consumer);
        await consumer.Start();

        var connection = new HubConnectionBuilder().WithUrl($"http://127.0.0.1:{port}/feed").Build();
        _connections.Add(connection);
        var received = new TaskCompletionSource<string>();
        connection.On<string>("Quote", q => received.TrySetResult(q));
        await connection.StartAsync();

        var pushEndpoint = Endpoint(component, $"127.0.0.1:{port}/feed", "mode=Server&method=Quote");
        var producer = pushEndpoint.CreateProducer();
        _producers.Add(producer);
        await producer.Start();
        await producer.Process(new Exchange(new Message("42")));

        (await received.Task.WaitAsync(TimeSpan.FromSeconds(10))).Should().Be("42");
    }

    // ── Волна 5: the documented recipe for putting group membership back after a reconnect ──

    [Fact]
    public async Task ConnectedEvent_RejoinsGroups()
    {
        var port = GetFreePort();
        var component = new SignalRComponent { ServerManager = _manager };
        var endpoint = Endpoint(component, $"127.0.0.1:{port}/rooms");
        await endpoint.Start();
        var consumer = endpoint.CreateConsumer(new RejoinProcessor("premium"));
        _consumers.Add(consumer);
        await consumer.Start();

        var connection = new HubConnectionBuilder().WithUrl($"http://127.0.0.1:{port}/rooms").Build();
        _connections.Add(connection);
        var received = new TaskCompletionSource<string>();
        connection.On<string>("Alert", a => received.TrySetResult(a));
        await connection.StartAsync();

        var pushEndpoint = Endpoint(component, $"127.0.0.1:{port}/rooms",
            "mode=Server&method=Alert&targetType=Group&targetGroup=premium");
        var producer = pushEndpoint.CreateProducer();
        _producers.Add(producer);
        await producer.Start();
        await producer.Process(new Exchange(new Message("members only")));

        (await received.Task.WaitAsync(TimeSpan.FromSeconds(10))).Should().Be("members only",
            "членство в группе принадлежит соединению и после reconnect теряется — README обещает, " +
            "что событие Connected с заголовком AddToGroup его возвращает");
    }

    /// <summary>The README recipe, as code: rejoin on the Connected event.</summary>
    private sealed class RejoinProcessor(string group) : IProcessor
    {
        public Task Process(IExchange exchange, CancellationToken ct = default)
        {
            if (exchange.In.GetHeader<string>(SignalRHeaders.Event) == "Connected")
            {
                exchange.Out = new Message(null);
                exchange.Out.Headers[SignalRHeaders.AddToGroup] = group;
            }

            return Task.CompletedTask;
        }
    }

    // ── SR-1. The producer kept no endpoint statistics ──

    [Fact]
    public async Task Producer_RecordsEndpointStatistics()
    {
        var port = GetFreePort();
        var component = new SignalRComponent { ServerManager = _manager };
        var endpoint = Endpoint(component, $"127.0.0.1:{port}/stats");
        await endpoint.Start();
        var consumer = endpoint.CreateConsumer(Substitute.For<IProcessor>());
        _consumers.Add(consumer);
        await consumer.Start();

        var pushEndpoint = Endpoint(component, $"127.0.0.1:{port}/stats", "mode=Server&method=Ping");
        var producer = pushEndpoint.CreateProducer();
        _producers.Add(producer);
        await producer.Start();
        await producer.Process(new Exchange(new Message("x")));

        // Ownership audit: MessagesOut belongs to the core (ToProcessor / the template), so a
        // hand-built producer records nothing here - self-recording double-counted in routes.
        ((IEndpointStatistics)pushEndpoint).MessagesOut.Should().Be(0,
            "MessagesOut пишет ядро, самозапись задваивала в маршруте");
    }

    // ── The producer name reached the log with the URI password in it ──

    [Fact]
    public void ProducerName_MasksPassword()
    {
        var component = new SignalRComponent { ServerManager = _manager };
        var endpoint = Endpoint(component, "user:s3cr3t@hub.example.com:5000/chat");
        var producer = (SignalRProducer)endpoint.CreateProducer();

        producer.DiagnosticName.Should().NotContain("s3cr3t");
    }

    // ── Helpers ──

    private string WriteSelfSignedPfx()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));

        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        san.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(san.Build());

        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        var path = Path.Combine(Path.GetTempPath(), $"redb-signalr-{Guid.NewGuid():N}.pfx");
        File.WriteAllBytes(path, cert.Export(X509ContentType.Pfx));
        _tempFiles.Add(path);
        return path;
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

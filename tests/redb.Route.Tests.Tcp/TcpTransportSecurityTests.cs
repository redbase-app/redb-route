using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Tcp;

namespace redb.Route.Tests.Tcp;

/// <summary>
/// The TCP server's TLS setup. It used to bind and accept happily with <c>ssl=true</c> and no
/// certificate, then kill every accepted connection deep in the accept loop with an
/// <c>ArgumentNullException</c> on <c>SslCertPath!</c> — a type not in its catch list. No plaintext
/// ever flowed (the stream becomes an SslStream only after a successful handshake), but the failure
/// belongs at the bind, with a message about the certificate, the way the shared HTTP host does it.
/// <para>The certificate is also loaded once at start now, not re-read from disk per connection.</para>
/// </summary>
public sealed class TcpTransportSecurityTests : IAsyncLifetime
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

    private TcpConsumer CreateConsumer(int port, Dictionary<string, string> parameters,
        IProcessor? processor = null, string host = "127.0.0.1")
    {
        var component = new TcpComponent();
        var uri = new EndpointUri("tcp", $"/{host}:{port}", $"tcp:{host}:{port}", parameters);
        var endpoint = (TcpEndpoint)component.CreateEndpoint(uri);
        var consumer = new TcpConsumer(endpoint, processor ?? Substitute.For<IProcessor>(), endpoint.EndpointOptions);
        _consumers.Add(consumer);
        return consumer;
    }

    private TcpProducer CreateProducer(int port, Dictionary<string, string> parameters, string host = "127.0.0.1")
    {
        var component = new TcpComponent();
        var uri = new EndpointUri("tcp", $"/{host}:{port}", $"tcp:{host}:{port}", parameters);
        var endpoint = (TcpEndpoint)component.CreateEndpoint(uri);
        var producer = (TcpProducer)endpoint.CreateProducer();
        _producers.Add(producer);
        return producer;
    }

    [Fact]
    public async Task TlsConsumer_WithoutCertificate_RefusesToStart()
    {
        var port = GetFreePort();
        var consumer = CreateConsumer(port, new Dictionary<string, string> { ["ssl"] = "true" });

        var act = () => consumer.Start();

        (await act.Should().ThrowAsync<InvalidOperationException>(
            "падать на каждом соединении ArgumentNullException'ом — не тот способ сообщить о missing сертификате"))
            .WithMessage("*certificate*");
    }

    [Fact]
    public async Task TlsConsumer_WithoutCertificate_LeavesNoListeningPort()
    {
        var port = GetFreePort();
        var consumer = CreateConsumer(port, new Dictionary<string, string> { ["ssl"] = "true" });

        var start = () => consumer.Start();
        await start.Should().ThrowAsync<InvalidOperationException>();

        using var client = new TcpClient();
        var connect = () => client.ConnectAsync(IPAddress.Loopback, port);
        await connect.Should().ThrowAsync<SocketException>(
            "слушатель принимал соединения, чтобы тут же их убить — порт снаружи выглядел живым");
    }

    [Fact]
    public async Task TlsConsumer_WithCertificate_CompletesTheHandshakeAndReceives()
    {
        var port = GetFreePort();
        var received = new TaskCompletionSource<string>();
        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                received.TrySetResult(ci.Arg<IExchange>().In.Body?.ToString() ?? "");
                return Task.CompletedTask;
            });

        var consumer = CreateConsumer(port, new Dictionary<string, string>
        {
            ["ssl"] = "true",
            ["sslCertPath"] = WritePfx(),
            ["framing"] = "TextLine",
        }, processor);
        await consumer.Start();

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        await using var ssl = new SslStream(client.GetStream(), leaveInnerStreamOpen: false,
            (_, _, _, _) => true);
        await ssl.AuthenticateAsClientAsync("localhost");

        var payload = Encoding.UTF8.GetBytes("over-tls\n");
        await ssl.WriteAsync(payload);
        await ssl.FlushAsync();

        (await received.Task.WaitAsync(TimeSpan.FromSeconds(10))).Should().Be("over-tls");
    }

    [Fact]
    public async Task TlsConsumer_ServesManyConnections_FromOneLoadOfTheCertificate()
    {
        var port = GetFreePort();
        var certPath = WritePfx();
        var consumer = CreateConsumer(port, new Dictionary<string, string>
        {
            ["ssl"] = "true",
            ["sslCertPath"] = certPath,
            ["framing"] = "TextLine",
        });
        await consumer.Start();

        // The PFX is gone from disk after the start. Every connection used to re-read it, so the
        // second client could never have completed a handshake.
        File.Delete(certPath);

        for (var i = 0; i < 2; i++)
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port);
            await using var ssl = new SslStream(client.GetStream(), leaveInnerStreamOpen: false,
                (_, _, _, _) => true);
            var handshake = () => ssl.AuthenticateAsClientAsync("localhost");
            await handshake.Should().NotThrowAsync(
                "сертификат читается один раз на старте, а не на каждое принятое соединение");
        }
    }

    // ── Bind address: the consumer parsed the host, the producer resolved it ──

    [Fact]
    public async Task Consumer_OnLocalhost_Starts()
    {
        var port = GetFreePort();
        var consumer = CreateConsumer(port, new Dictionary<string, string> { ["framing"] = "TextLine" },
            host: "localhost");

        await consumer.Start();

        using var client = new TcpClient();
        await client.ConnectAsync("localhost", port);
        client.Connected.Should().BeTrue(
            "IPAddress.Parse(\"localhost\") роняло консьюмера FormatException'ом, хотя продюсер то же имя резолвит");
    }

    [Fact]
    public async Task Consumer_OnUnresolvableHost_FailsWithANamedError()
    {
        var port = GetFreePort();
        var consumer = CreateConsumer(port, [], host: "no-such-host.invalid");

        var act = () => consumer.Start();

        (await act.Should().ThrowAsync<ArgumentException>()).WithMessage("*no-such-host.invalid*");
    }

    // ── Producer: reaching a server with a self-signed certificate ──

    [Fact]
    public async Task Producer_TrustAllCertificates_ReachesASelfSignedServer()
    {
        var port = GetFreePort();
        var received = new TaskCompletionSource<string>();
        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                received.TrySetResult(ci.Arg<IExchange>().In.Body?.ToString() ?? "");
                return Task.CompletedTask;
            });

        var consumer = CreateConsumer(port, new Dictionary<string, string>
        {
            ["ssl"] = "true",
            ["sslCertPath"] = WritePfx(),
            ["framing"] = "TextLine",
        }, processor);
        await consumer.Start();

        var producer = CreateProducer(port, new Dictionary<string, string>
        {
            ["ssl"] = "true",
            ["trustAllCertificates"] = "true",
            ["sslTargetHost"] = "localhost",
            ["framing"] = "TextLine",
        });
        await producer.Start();
        await producer.Process(new Exchange(new Message("self-signed")));

        (await received.Task.WaitAsync(TimeSpan.FromSeconds(10))).Should().Be("self-signed",
            "у продюсера не было способа принять самоподписанный сертификат вовсе: staging недостижим");
    }

    [Fact]
    public async Task Producer_WithoutTrustAll_RefusesASelfSignedServer()
    {
        var port = GetFreePort();
        var consumer = CreateConsumer(port, new Dictionary<string, string>
        {
            ["ssl"] = "true",
            ["sslCertPath"] = WritePfx(),
            ["framing"] = "TextLine",
        });
        await consumer.Start();

        var producer = CreateProducer(port, new Dictionary<string, string>
        {
            ["ssl"] = "true",
            ["sslTargetHost"] = "localhost",
            ["framing"] = "TextLine",
        });

        var act = () => producer.Start();
        // The specific type matters: ThrowAsync<Exception> passed on ANY failure (a dead consumer,
        // a socket error) and could not tell "refused the certificate" from "never connected".
        await act.Should().ThrowAsync<System.Security.Authentication.AuthenticationException>(
            "отключение проверки сертификата обязано быть явным решением, а не поведением по умолчанию");
    }

    // ── Producer mTLS: presenting a client certificate ──

    [Fact]
    public async Task Producer_PresentsClientCertificate_WhenTheServerAsksForOne()
    {
        var port = GetFreePort();
        var serverCert = LoadPfx(WritePfx());
        var clientCertPath = WritePfx("tcp-client");

        // A raw mTLS server: this connector's own consumer does not request client certificates
        // (AuthenticateAsServerAsync(cert) alone), so the counterpart here is a plain SslStream.
        using var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        var presented = new TaskCompletionSource<string?>();
        var accept = Task.Run(async () =>
        {
            using var server = await listener.AcceptTcpClientAsync();
            await using var ssl = new SslStream(server.GetStream(), leaveInnerStreamOpen: false,
                (_, _, _, _) => true);
            try
            {
                await ssl.AuthenticateAsServerAsync(serverCert, clientCertificateRequired: true,
                    checkCertificateRevocation: false);
                presented.TrySetResult(ssl.RemoteCertificate?.Subject);
            }
            catch (Exception ex)
            {
                presented.TrySetException(ex);
            }
        });

        var producer = CreateProducer(port, new Dictionary<string, string>
        {
            ["ssl"] = "true",
            ["trustAllCertificates"] = "true",
            ["sslTargetHost"] = "localhost",
            ["clientCertPath"] = clientCertPath,
            ["framing"] = "TextLine",
        });
        await producer.Start();

        (await presented.Task.WaitAsync(TimeSpan.FromSeconds(10)))
            .Should().Contain("tcp-client",
                "продюсер не умел предъявлять клиентский сертификат вовсе — сервер с mTLS был недостижим");

        await accept;
        listener.Stop();
        serverCert.Dispose();
    }

#pragma warning disable SYSLIB0057 // net8.0 is a target framework; X509CertificateLoader is net9+.
    private static X509Certificate2 LoadPfx(string path) => new(path);
#pragma warning restore SYSLIB0057

    private string WritePfx(string cn = "localhost")
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest($"CN={cn}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));

        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        san.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(san.Build());

        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        var path = Path.Combine(Path.GetTempPath(), $"redb-tcp-tls-{Guid.NewGuid():N}.pfx");
        File.WriteAllBytes(path, cert.Export(X509ContentType.Pfx));
        _tempFiles.Add(path);
        return path;
    }
}

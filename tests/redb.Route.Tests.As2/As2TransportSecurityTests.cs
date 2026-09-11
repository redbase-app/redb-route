using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using FluentAssertions;
using redb.Route.Abstractions;
using redb.Route.As2;
using redb.Route.Core;
using redb.Route.Http;
using As2Dsl = redb.Route.As2.Fluent.As2;

namespace redb.Route.Tests.As2;

/// <summary>
/// The AS2 receiver's transport security. Until Ф14 the connector passed <c>useTls</c> to the
/// shared host and no certificate with it — there was no server-certificate option at all — so an
/// <c>as2s://</c> receiver opened a PLAINTEXT port while <see cref="As2Endpoint"/> advertised
/// <c>https://</c> to the trading partner as its PartnerUrl.
/// </summary>
public sealed class As2TransportSecurityTests : IDisposable
{
    private readonly List<string> _tempFiles = [];

    public void Dispose()
    {
        foreach (var f in _tempFiles) if (File.Exists(f)) File.Delete(f);
    }

    private static int FreePort()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    [Fact]
    public async Task TlsReceiver_WithoutCertificate_RefusesToStart()
    {
        var port = FreePort();
        var component = new As2Component();
        var endpoint = component.CreateEndpoint(
            EndpointUriParser.Parse(As2Dsl.Receive("/inbound").Host("127.0.0.1").Port(port).Tls()));
        var consumer = endpoint.CreateConsumer(Substitute.For<IProcessor>());

        var act = () => consumer.Start();

        (await act.Should().ThrowAsync<InvalidOperationException>(
            "as2s поднимал открытый сокет, а PartnerUrl обещал партнёру https://"))
            .WithMessage("*certificate*");
    }

    [Fact]
    public async Task TlsReceiver_WithoutCertificate_LeavesNoPlaintextPortOpen()
    {
        var port = FreePort();
        await using var context = new RouteContext();
        context.AddComponent(new As2Component());
        context.AddRoutes(r =>
            r.From(As2Dsl.Receive("/inbound").Host("127.0.0.1").Port(port).Tls()).Process(_ => { }));

        // The context does not propagate a consumer's start failure — one bad route must not stop
        // the rest — so the observable security property is the one that matters: the port that a
        // partner would have taken for a TLS endpoint is simply not there.
        await context.Start();

        using var client = new HttpClient();
        var plain = () => client.PostAsync($"http://127.0.0.1:{port}/inbound", new StringContent("x"));
        await plain.Should().ThrowAsync<HttpRequestException>(
            "именно этот открытый порт партнёр и принимал за TLS-эндпоинт");
    }

    [Fact]
    public async Task TlsReceiver_WithCertificate_ActuallyTerminatesTls()
    {
        var port = FreePort();
        var certPath = WritePfx();

        await using var context = new RouteContext();
        context.AddComponent(new As2Component());
        context.AddRoutes(r =>
            r.From(As2Dsl.Receive("/inbound").Host("127.0.0.1").Port(port).Tls(certPath)).Process(_ => { }));
        await context.Start();

        using var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
        };
        using var client = new HttpClient(handler);

        // The AS2 handler will reject the body; what matters here is that the TLS handshake
        // completed at all, which it could not before there was a certificate to present.
        var response = await client.PostAsync($"https://127.0.0.1:{port}/inbound", new StringContent("not-as2"));
        response.Should().NotBeNull();
    }

    [Fact]
    public async Task TlsReceiver_TakesCertificateFromTheConnectionFactory()
    {
        var port = FreePort();

        await using var context = new RouteContext();
        context.AddComponent(new As2Component());
        // The password belongs in the registry, not the route URI — the framework-wide pattern.
        context.AddToRegistry("partner", new As2ConnectionFactory
        {
            As2From = "us",
            As2To = "them",
            Sign = false,
            Encrypt = false,
            SslCertPath = WritePfx(),
        });

        context.AddRoutes(r =>
            r.From(As2Dsl.Receive("/inbound").Host("127.0.0.1").Port(port)
                    .ConnectionFactory("partner").Tls())
                .Process(_ => { }));

        await context.Start();

        using var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
        };
        using var client = new HttpClient(handler);
        var response = await client.PostAsync($"https://127.0.0.1:{port}/inbound", new StringContent("not-as2"));
        response.Should().NotBeNull();
    }

    private string WritePfx()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));

        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        san.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(san.Build());

        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        var path = Path.Combine(Path.GetTempPath(), $"redb-as2-tls-{Guid.NewGuid():N}.pfx");
        File.WriteAllBytes(path, cert.Export(X509ContentType.Pfx));
        _tempFiles.Add(path);
        return path;
    }
}

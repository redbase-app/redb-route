using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using redb.Route.Http;
using Xunit;

namespace redb.Route.Tests.Hosting;

/// <summary>
/// The invariant every comparable stack enforces (nginx, httpd, Jetty, Spring Boot, and Kestrel's
/// own <c>UseHttps()</c>): asking for TLS selects the socket, and a certificate that cannot be
/// resolved fails the bind. Nobody flips the socket back to plaintext — this host used to, and
/// then reported <c>https://</c> for it.
/// <para>
/// Where the certificate comes from is a separate question: the endpoint, a named connection
/// factory, or the host-wide default configured here (the shape Camel's SSLContextParameters and
/// Spring Boot's SSL bundles have). Only "nowhere" is an error.
/// </para>
/// </summary>
public sealed class SharedHostTlsTests : IAsyncLifetime
{
    private readonly List<SharedHttpServerManager> _managers = [];
    private readonly List<string> _tempFiles = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var m in _managers) await m.DisposeAsync();
        foreach (var f in _tempFiles) if (File.Exists(f)) File.Delete(f);
    }

    private SharedHttpServerManager Manager(HttpHostingOptions? options = null)
    {
        var manager = new SharedHttpServerManager(options);
        _managers.Add(manager);
        return manager;
    }

    private static int GetFreePort()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private static HttpClient TrustingClient() => new(new HttpClientHandler
    {
        ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
    });

    [Fact]
    public async Task SslWithoutAnyCertificate_RefusesToBind()
    {
        var port = GetFreePort();
        var manager = Manager();
        manager.RegisterRoute("127.0.0.1", port, "/x", "GET", ctx => ctx.Response.WriteAsync("x"), ssl: true);

        var act = () => manager.EnsureStarted("127.0.0.1", port);

        (await act.Should().ThrowAsync<InvalidOperationException>(
            "открытый сокет под вывеской https:// — это не «TLS выключен», это молчаливый даунгрейд"))
            .WithMessage("*certificate*");
    }

    [Fact]
    public async Task SslWithoutAnyCertificate_DoesNotLeaveAPlaintextPortOpen()
    {
        var port = GetFreePort();
        var manager = Manager();
        manager.RegisterRoute("127.0.0.1", port, "/x", "GET", ctx => ctx.Response.WriteAsync("x"), ssl: true);

        var start = () => manager.EnsureStarted("127.0.0.1", port);
        await start.Should().ThrowAsync<InvalidOperationException>();

        using var client = new HttpClient();
        var plain = () => client.GetStringAsync($"http://127.0.0.1:{port}/x");
        await plain.Should().ThrowAsync<HttpRequestException>("порт вообще не должен был открыться");
    }

    [Fact]
    public async Task EndpointCertificate_TerminatesTls()
    {
        var port = GetFreePort();
        var manager = Manager();
        manager.RegisterRoute("127.0.0.1", port, "/x", "GET", ctx => ctx.Response.WriteAsync("x"),
            ssl: true, sslCertPath: WritePfx());

        await manager.EnsureStarted("127.0.0.1", port);

        using var client = TrustingClient();
        (await client.GetStringAsync($"https://127.0.0.1:{port}/x")).Should().Be("x");
    }

    [Fact]
    public async Task HostDefaultCertificate_IsUsedWhenTheEndpointHasNone()
    {
        var port = GetFreePort();
        var options = new HttpHostingOptions();
        options.Tls.DefaultCertificatePath = WritePfx();

        var manager = Manager(options);
        // No certificate on the route: it resolves from the host, the way a Camel endpoint resolves
        // from global SSLContextParameters.
        manager.RegisterRoute("127.0.0.1", port, "/x", "GET", ctx => ctx.Response.WriteAsync("x"), ssl: true);

        await manager.EnsureStarted("127.0.0.1", port);

        using var client = TrustingClient();
        (await client.GetStringAsync($"https://127.0.0.1:{port}/x")).Should().Be("x");
    }

    [Fact]
    public async Task HostDefaultCertificate_CanBeAnInstance()
    {
        var port = GetFreePort();
        var options = new HttpHostingOptions();
        options.Tls.DefaultCertificate = LoadPfx(WritePfx());

        var manager = Manager(options);
        manager.RegisterRoute("127.0.0.1", port, "/x", "GET", ctx => ctx.Response.WriteAsync("x"), ssl: true);

        await manager.EnsureStarted("127.0.0.1", port);

        using var client = TrustingClient();
        (await client.GetStringAsync($"https://127.0.0.1:{port}/x")).Should().Be("x");
    }

    [Fact]
    public async Task EndpointCertificate_WinsOverTheHostDefault()
    {
        var port = GetFreePort();
        var options = new HttpHostingOptions();
        options.Tls.DefaultCertificatePath = WritePfx("host-default");

        var manager = Manager(options);
        manager.RegisterRoute("127.0.0.1", port, "/x", "GET", ctx => ctx.Response.WriteAsync("x"),
            ssl: true, sslCertPath: WritePfx("endpoint"));

        await manager.EnsureStarted("127.0.0.1", port);

        string? subject = null;
        using var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, cert, _, _) => { subject = cert?.Subject; return true; },
        };
        using var client = new HttpClient(handler);
        await client.GetStringAsync($"https://127.0.0.1:{port}/x");

        subject.Should().Contain("endpoint", "явный сертификат маршрута перекрывает дефолт хоста");
    }

    [Fact]
    public async Task PlainListener_IsUnaffectedByAHostDefault()
    {
        var options = new HttpHostingOptions();
        options.Tls.DefaultCertificatePath = WritePfx();

        var port = GetFreePort();
        var manager = Manager(options);
        manager.RegisterRoute("127.0.0.1", port, "/x", "GET", ctx => ctx.Response.WriteAsync("x"));

        // ssl был и остаётся явным решением: дефолтный сертификат ничего не включает сам по себе.
        var act = () => manager.EnsureStarted("127.0.0.1", port);
        // The assertion used to be fire-and-forget (no await): it raced the actual bind.
        await act.Should().NotThrowAsync();
        manager.GetBaseUrl("127.0.0.1", port).Should().StartWith("http://");
    }

    // ── Helpers ──

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
        var path = Path.Combine(Path.GetTempPath(), $"redb-host-{Guid.NewGuid():N}.pfx");
        File.WriteAllBytes(path, cert.Export(X509ContentType.Pfx));
        _tempFiles.Add(path);
        return path;
    }

#pragma warning disable SYSLIB0057 // net8.0 is a target framework; X509CertificateLoader is net9+.
    private static X509Certificate2 LoadPfx(string path) => new(path);
#pragma warning restore SYSLIB0057
}

using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using redb.Route.Abstractions;
using redb.Route.As4;
using redb.Route.As4.Messaging;
using redb.Route.As4.Mime;
using redb.Route.As4.Security;
using redb.Route.Components;
using redb.Route.Core;
using redb.Route.Processors;

namespace redb.Route.Tests.As4;

/// <summary>
/// Ф7: TLS and mutual TLS of the AS4 receiver on real loopback handshakes, after <c>As2TransportSecurityTests</c> and
/// <c>SoapTransportSecurityTests</c>: a TLS receiver without a certificate refuses to start (as the SOAP receiver), one
/// with a certificate terminates TLS and leaves no plaintext port, the certificate may come from the node, and a
/// receiver that requires a client certificate refuses a handshake without one or with one outside its allow-list.
/// </summary>
public sealed class As4TransportSecurityTests : IDisposable
{
    private static readonly X509Certificate2 NodeA = As4TestMessages.KeyPair("CN=a.as4.test");
    private static readonly X509Certificate2 NodeB = As4TestMessages.KeyPair("CN=b.as4.test");
    private static readonly X509Certificate2 PartnerTls = As4TestMessages.KeyPair("CN=a.tls.as4.test");
    private static readonly X509Certificate2 StrangerTls = As4TestMessages.KeyPair("CN=stranger.tls.as4.test");
    private const string Payload = "<invoice xmlns=\"urn:example\"><total>42</total></invoice>";

    private readonly List<string> _tempFiles = [];

    public void Dispose()
    {
        foreach (var file in _tempFiles)
            File.Delete(file);
    }

    [Fact]
    public async Task TlsReceiver_WithoutCertificate_RefusesToStart()
    {
        using var context = NewContext(out _);
        var consumer = context.GetEndpoint($"as4s:/as4/in?host=127.0.0.1&port={TestPort()}&connectionFactory=b&idempotentRepository=dedup")
            .CreateConsumer(Substitute.For<IProcessor>());

        var act = () => consumer.Start();

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*TLS but has no certificate*");
    }

    [Fact]
    public async Task ClientCertificatesFromTheNode_WithoutTls_RefuseToStart()
    {
        using var context = NewContext(out var node);
        node.ClientCertificateMode = As4ClientCertificateMode.RequireCertificate;
        var consumer = context.GetEndpoint($"as4:/as4/in?host=127.0.0.1&port={TestPort()}&connectionFactory=b&idempotentRepository=dedup")
            .CreateConsumer(Substitute.For<IProcessor>());

        var act = () => consumer.Start();

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*client certificates without TLS*");
    }

    [Fact]
    public async Task TlsReceiver_TerminatesTls_AndLeavesNoPlaintextPort()
    {
        var port = TestPort();
        using var context = NewContext(out _);
        var consumer = await StartConsumer(context, port, $"&sslCertPath={Uri.EscapeDataString(WritePfx())}");
        await using var stop = Stop(consumer);

        var (status, errorCode, receipt) = await Post(Https(), $"https://127.0.0.1:{port}/as4/in");
        (status, errorCode, receipt).Should().Be((HttpStatusCode.OK, (string?)null, true));

        var plain = () => Post(new HttpClientHandler(), $"http://127.0.0.1:{port}/as4/in");
        await plain.Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task TlsReceiver_TakesTheCertificateFromTheNode()
    {
        var port = TestPort();
        using var context = NewContext(out var node);
        node.SslCertPath = WritePfx();
        var consumer = await StartConsumer(context, port, extraQuery: "");
        await using var stop = Stop(consumer);

        (await Post(Https(), $"https://127.0.0.1:{port}/as4/in")).Receipt.Should().BeTrue();
    }

    [Fact]
    public async Task RequiredClientCertificate_OnTheAllowList_IsAccepted_AndReachesTheExchange()
    {
        var port = TestPort();
        using var context = NewContext(out _);
        IExchange? received = null;
        var consumer = await StartConsumer(context, port,
            $"&sslCertPath={Uri.EscapeDataString(WritePfx())}&clientCertificateMode=RequireCertificate&allowedClientThumbprints={PartnerTls.Thumbprint}",
            e => received = e);
        await using var stop = Stop(consumer);

        var handler = Https();
        handler.ClientCertificates.Add(PartnerTls);
        (await Post(handler, $"https://127.0.0.1:{port}/as4/in")).Receipt.Should().BeTrue();

        received!.In.GetHeader<string>(As4Headers.ClientCertThumbprint).Should().Be(PartnerTls.Thumbprint);
        received.In.GetHeader<string>(As4Headers.ClientCertSubject).Should().Be(PartnerTls.Subject);
        var identities = ExchangePrincipal.Get(received)!.Identities.ToList();
        identities.Select(i => i.AuthenticationType).Should().Equal(As4AuthenticationTypes.Signature, As4AuthenticationTypes.ClientCertificate);
        identities[1].FindFirst(System.Security.Claims.ClaimTypes.Thumbprint)!.Value.Should().Be(PartnerTls.Thumbprint);
    }

    [Fact]
    public async Task RequiredClientCertificate_Missing_IsRefusedInTheHandshake()
    {
        var port = TestPort();
        using var context = NewContext(out _);
        var consumer = await StartConsumer(context, port,
            $"&sslCertPath={Uri.EscapeDataString(WritePfx())}&clientCertificateMode=RequireCertificate&allowedClientThumbprints={PartnerTls.Thumbprint}");
        await using var stop = Stop(consumer);

        var act = () => Post(Https(), $"https://127.0.0.1:{port}/as4/in");

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task RequiredClientCertificate_OffTheAllowList_IsRefusedInTheHandshake()
    {
        var port = TestPort();
        using var context = NewContext(out var node);
        node.ClientCertificateMode = As4ClientCertificateMode.RequireCertificate;   // from the node, this time
        node.AllowedClientThumbprints = PartnerTls.Thumbprint;
        var consumer = await StartConsumer(context, port, $"&sslCertPath={Uri.EscapeDataString(WritePfx())}");
        await using var stop = Stop(consumer);

        var handler = Https();
        handler.ClientCertificates.Add(StrangerTls);
        var act = () => Post(handler, $"https://127.0.0.1:{port}/as4/in");

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task RequiredClientCertificate_OnTheAllowListButExpired_IsRefusedInTheHandshake()
    {
        // The allow-list replaces Kestrel's check, and a pin is no excuse for an expired key (Holodeck, Domibus).
        var expired = As4TestMessages.KeyPair("CN=a.tls.as4.test", DateTimeOffset.UtcNow.AddYears(-2), DateTimeOffset.UtcNow.AddDays(-1));
        var port = TestPort();
        using var context = NewContext(out _);
        var consumer = await StartConsumer(context, port,
            $"&sslCertPath={Uri.EscapeDataString(WritePfx())}&clientCertificateMode=RequireCertificate&allowedClientThumbprints={expired.Thumbprint}");
        await using var stop = Stop(consumer);

        var handler = Https();
        handler.ClientCertificates.Add(expired);
        var act = () => Post(handler, $"https://127.0.0.1:{port}/as4/in");

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    // ── TLS versions (sending) ───────────────────────────────────────────────

    [Fact]
    public void Sender_OffersTls12And13_ByDefault()
    {
        // eDelivery AS4 1.16: TLS 1.2 MUST, 1.3 MAY; Holodeck, phase4 and Domibus (CXF "TLS") default to exactly these.
        using var handler = As4Producer.CreateHandler(Node());
        handler.SslProtocols.Should().Be(System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13);
    }

    [Fact]
    public void Sender_OffersWhatTheNodeAllows()
    {
        var node = Node();
        node.SslProtocols = System.Security.Authentication.SslProtocols.Tls13;
        using var handler = As4Producer.CreateHandler(node);
        handler.SslProtocols.Should().Be(System.Security.Authentication.SslProtocols.Tls13);
    }

    [Theory]
#pragma warning disable SYSLIB0039, CS0618 // the obsolete protocols are what the check must refuse
    [InlineData(System.Security.Authentication.SslProtocols.Tls11 | System.Security.Authentication.SslProtocols.Tls12)]
    [InlineData(System.Security.Authentication.SslProtocols.Tls)]
    [InlineData(System.Security.Authentication.SslProtocols.Ssl3)]
#pragma warning restore SYSLIB0039, CS0618
    [InlineData(System.Security.Authentication.SslProtocols.None)]
    public void Node_WithAProtocolTheProfileForbids_IsRefused(System.Security.Authentication.SslProtocols protocols)
    {
        var node = Node();
        node.SslProtocols = protocols;

        var act = () => node.Validate("b");

        act.Should().Throw<InvalidOperationException>().WithMessage("*SslProtocols*");
    }

    // ── Fixtures ─────────────────────────────────────────────────────────────

    private static As4ConnectionFactory Node()
    {
        using var context = NewContext(out var node);
        return node;
    }

    private static int TestPort() => global::redb.Route.Tests.Shared.TestPorts.Next();

    private static RouteContext NewContext(out As4ConnectionFactory nodeB)
    {
        var context = new RouteContext();
        context.AddComponent(new As4Component());
        context.AddIdempotentRepository("dedup", new InMemoryIdempotentRepository());
        nodeB = new As4ConnectionFactory
        {
            OurPartyId = "urn:redb:b",
            ExternalHostName = "b.example",
            SigningCertificate = NodeB,
            DecryptionCertificates = { NodeB },
            Partners =
            {
                new As4Partner
                {
                    Name = "a",
                    PartyId = "urn:redb:a",
                    Service = "urn:example",
                    Action = "Submit",
                    PartnerSigningCertificates = { As4TestMessages.PublicOnly(NodeA) },
                    PartnerEncryptionCertificate = As4TestMessages.PublicOnly(NodeA),
                },
            },
        };
        context.AddToRegistry("b", nodeB);
        return context;
    }

    private static async Task<IConsumer> StartConsumer(RouteContext context, int port, string extraQuery, Action<IExchange>? onExchange = null)
    {
        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(ci => { onExchange?.Invoke(ci.Arg<IExchange>()); return Task.CompletedTask; });
        var consumer = context.GetEndpoint($"as4s:/as4/in?host=127.0.0.1&port={port}&connectionFactory=b&idempotentRepository=dedup{extraQuery}")
            .CreateConsumer(processor);
        await consumer.Start();
        return consumer;
    }

    private static AsyncStop Stop(IConsumer consumer) => new(consumer);

    private readonly struct AsyncStop(IConsumer consumer) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await consumer.Stop();
    }

    /// <summary>A client that trusts the test's self-signed server certificate, as the AS2 TLS tests do.</summary>
    private static HttpClientHandler Https() => new()
    {
        ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
    };

    private static async Task<(HttpStatusCode Status, string? ErrorCode, bool Receipt)> Post(HttpClientHandler handler, string url)
    {
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        var (contentType, body) = SecuredMessage();
        var content = new ByteArrayContent(body);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        using var response = await http.PostAsync(url, content);
        var answer = SwaMessage.Read(response.Content.Headers.ContentType!.ToString(), await response.Content.ReadAsByteArrayAsync(), 1024 * 1024);
        var signals = MessagingReader.Read(answer.Envelope).SignalMessages;
        return (response.StatusCode, signals.SelectMany(s => s.Errors).FirstOrDefault()?.ErrorCode, signals.Any(s => s.IsReceipt));
    }

    /// <summary>A user message from A to B, gzipped, signed and encrypted as A sends it.</summary>
    private static (string ContentType, byte[] Body) SecuredMessage()
    {
        const string contentId = "p1@a.example";
        using var message = SwaMessage.Create(As4TestMessages.UserMessage(Guid.NewGuid().ToString("N") + "@a.example", contentId,
            from: "urn:redb:a", to: "urn:redb:b", service: "urn:example", action: "Submit"));
        message.AddPart(contentId, "application/gzip", As4TestMessages.Gzip(Encoding.UTF8.GetBytes(Payload)));
        As4SecurityEngine.Sign(message, NodeA, As4KeyReference.BinarySecurityToken, null, DateTimeOffset.UtcNow);
        As4SecurityEngine.Encrypt(message, As4TestMessages.PublicOnly(NodeB), As4KeyReference.BinarySecurityToken);
        return message.Write();
    }

    /// <summary>A self-signed localhost server certificate in a temporary PFX, as the AS2 TLS tests write it.</summary>
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
        var path = Path.Combine(Path.GetTempPath(), $"redb-as4-tls-{Guid.NewGuid():N}.pfx");
        File.WriteAllBytes(path, cert.Export(X509ContentType.Pfx));
        _tempFiles.Add(path);
        return path;
    }
}

using System.Diagnostics;
using System.Net.Sockets;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using FluentAssertions;
using MimeKit;
using redb.Route.Abstractions;
using redb.Route.As2;
using redb.Route.As2.Crypto;
using redb.Route.Core;
using As2Dsl = redb.Route.As2.Fluent.As2;

namespace redb.Route.Tests.As2;

/// <summary>
/// Ф3 end-to-end tests: a real AS2 message travels over real HTTP (shared Kestrel) into the consumer,
/// which decrypts + verifies + delivers the payload to a route and returns a synchronous MDN. One test
/// drives the full producer→consumer loop; the other asserts the MDN structure. Loopback uses one
/// certificate for both sides (distinct certs is the real scenario, validated in Ф8).
/// </summary>
public class As2ReceiveTests
{
    private static readonly X509Certificate2 Cert = MakeCert("as2-loopback");

    [Fact]
    public async Task Loopback_ProducerToConsumer_DeliversPayload()
    {
        var port = FreePort();
        await using var context = new RouteContext();
        context.AddComponent(new As2Component());
        context.AddToRegistry("me", Factory());

        var received = new List<string>();
        context.AddRoutes(r =>
            r.From(As2Dsl.Receive("/inbound").Host("127.0.0.1").Port(port).ConnectionFactory("me"))
                .Process(e => received.Add(Encoding.UTF8.GetString((byte[])e.In.Body!))));

        await context.Start();

        var producer = context.GetEndpoint(As2Dsl.Send($"http://127.0.0.1:{port}/inbound").ConnectionFactory("me")).CreateProducer();
        await producer.Start();
        await producer.Process(new Exchange(new Message("ISA*00*...EDI~") { ContentType = "application/edi-x12" }));

        // The consumer decrypted + verified + delivered the original business payload to the route.
        received.Should().ContainSingle().Which.Should().Be("ISA*00*...EDI~");
    }

    [Fact]
    public async Task Receive_ReturnsSyncMdn_WithDispositionAndMic()
    {
        var port = FreePort();
        await using var context = new RouteContext();
        context.AddComponent(new As2Component());
        context.AddToRegistry("me", Factory());
        context.AddRoutes(r =>
            r.From(As2Dsl.Receive("/inbound").Host("127.0.0.1").Port(port).ConnectionFactory("me")).Process(_ => { }));
        await context.Start();

        // Build a signed+encrypted AS2 message with the real engine and POST it directly to capture the MDN.
        var (contentType, transferEncoding, body) = BuildAs2Message("PO*4200~");
        var messageId = "<probe-123@redb.route>";

        using var client = new HttpClient();
        using var content = new ByteArrayContent(body);
        content.Headers.TryAddWithoutValidation("Content-Type", contentType);
        content.Headers.TryAddWithoutValidation("Content-Transfer-Encoding", transferEncoding);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}/inbound") { Content = content };
        request.Headers.TryAddWithoutValidation("AS2-From", "THEM");
        request.Headers.TryAddWithoutValidation("AS2-To", "US");
        request.Headers.TryAddWithoutValidation("Message-ID", messageId);

        var response = await client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        // SignedMdn is on, so the MDN is a multipart/signed wrapping the multipart/report.
        response.Content.Headers.ContentType!.MediaType.Should().Be("multipart/signed");

        // The report is a readable (signed, not encrypted) part inside — its fields are in the body.
        var mdn = await response.Content.ReadAsStringAsync();
        mdn.Should().Contain("processed");
        mdn.Should().Contain("Received-Content-MIC");
        mdn.Should().Contain(messageId);   // Original-Message-ID echoed back
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Loopback_ProducerReceivesSignedMdn_SignatureAndMicVerified()
    {
        var port = FreePort();
        await using var context = new RouteContext();
        context.AddComponent(new As2Component());
        context.AddToRegistry("me", Factory());
        context.AddRoutes(r =>
            r.From(As2Dsl.Receive("/inbound").Host("127.0.0.1").Port(port).ConnectionFactory("me")).Process(_ => { }));
        await context.Start();

        var producer = context.GetEndpoint(As2Dsl.Send($"http://127.0.0.1:{port}/inbound").ConnectionFactory("me")).CreateProducer();
        await producer.Start();
        var exchange = new Exchange(new Message("PO*4200~") { ContentType = "application/edi-x12" });
        await producer.Process(exchange);

        // The producer parsed the signed MDN: signature verified and the returned MIC matched what we sent.
        exchange.HasOut.Should().BeTrue();
        exchange.Out!.GetHeader<string>(As2Headers.MdnDisposition).Should().Contain("processed");
        exchange.Out!.GetHeader<bool>(As2Headers.SignatureValid).Should().BeTrue();
        exchange.Out!.GetHeader<bool>(As2Headers.MdnMicMatch).Should().BeTrue();
    }

    [Fact]
    public async Task AsyncMdn_Loopback_CorrelatesAndDeliversReceipt()
    {
        var port = FreePort();
        await using var context = new RouteContext();
        context.AddComponent(new As2Component());
        context.AddToRegistry("me", new As2ConnectionFactory
        {
            OurCertificate = Cert, PartnerCertificate = Cert, As2From = "US", As2To = "THEM",
            Sign = true, Encrypt = true, SignedMdn = true,
            MdnMode = As2MdnMode.Async, AsyncMdnUrl = $"http://127.0.0.1:{port}/mdn",
        });

        var received = new List<string>();
        var mdns = new List<(string? original, string? disposition, bool micMatch)>();
        context.AddRoutes(r =>
        {
            r.From(As2Dsl.Receive("/inbound").Host("127.0.0.1").Port(port).ConnectionFactory("me"))
                .Process(e => received.Add(Encoding.UTF8.GetString((byte[])e.In.Body!)));
            r.From(As2Dsl.ReceiveMdn("/mdn").Host("127.0.0.1").Port(port).ConnectionFactory("me"))
                .Process(e => mdns.Add((
                    e.In.GetHeader<string>(As2Headers.MessageId),
                    e.In.GetHeader<string>(As2Headers.MdnDisposition),
                    e.In.GetHeader<bool>(As2Headers.MdnMicMatch))));
        });
        await context.Start();

        var producer = context.GetEndpoint(As2Dsl.Send($"http://127.0.0.1:{port}/inbound").ConnectionFactory("me")).CreateProducer();
        await producer.Start();
        var exchange = new Exchange(new Message("PO*ASYNC~") { ContentType = "application/edi-x12" });
        await producer.Process(exchange);
        var sentId = exchange.In.GetHeader<string>(As2Headers.MessageId);

        // Payload delivered to the business route; the async MDN came back, correlated by Original-Message-ID.
        received.Should().ContainSingle().Which.Should().Be("PO*ASYNC~");
        mdns.Should().ContainSingle();
        mdns[0].original.Should().Be(sentId);
        mdns[0].disposition.Should().Contain("processed");
        mdns[0].micMatch.Should().BeTrue();
    }

    [Fact]
    public async Task Unsigned_Message_Rejected_When_Signature_Required()
    {
        var port = FreePort();
        await using var context = new RouteContext();
        context.AddComponent(new As2Component());
        // The receiver requires a signature; the sender does not sign.
        context.AddToRegistry("recv", new As2ConnectionFactory
        {
            OurCertificate = Cert, PartnerCertificate = Cert, As2From = "THEM", As2To = "US",
            Sign = true, Encrypt = false, MdnMode = As2MdnMode.Sync, SignedMdn = false,
        });
        context.AddToRegistry("send", new As2ConnectionFactory
        {
            OurCertificate = Cert, PartnerCertificate = Cert, As2From = "US", As2To = "THEM",
            Sign = false, Encrypt = false, MdnMode = As2MdnMode.Sync, SignedMdn = false,
        });

        var delivered = new List<string>();
        context.AddRoutes(r =>
            r.From(As2Dsl.Receive("/in").Host("127.0.0.1").Port(port).ConnectionFactory("recv"))
                .Process(e => delivered.Add(Encoding.UTF8.GetString((byte[])e.In.Body!))));
        await context.Start();

        var producer = context.GetEndpoint(As2Dsl.Send($"http://127.0.0.1:{port}/in").ConnectionFactory("send")).CreateProducer();
        await producer.Start();
        await producer.Process(new Exchange(new Message("PO*UNSIGNED~") { ContentType = "application/edi-x12" }));

        // The unsigned message must NOT reach the business route (rejected with a negative MDN).
        delivered.Should().BeEmpty();
    }

    [Fact]
    public async Task Loopback_RecordsConsumerStatistics()
    {
        var port = FreePort();
        await using var context = new RouteContext();
        context.AddComponent(new As2Component());
        context.AddToRegistry("me", Factory());
        context.AddRoutes(r =>
            r.From(As2Dsl.Receive("/inbound").Host("127.0.0.1").Port(port).ConnectionFactory("me")).Process(_ => { }));
        await context.Start();

        var producer = context.GetEndpoint(As2Dsl.Send($"http://127.0.0.1:{port}/inbound").ConnectionFactory("me")).CreateProducer();
        await producer.Start();
        await producer.Process(new Exchange(new Message("stats~") { ContentType = "application/edi-x12" }));

        var stats = (IEndpointStatistics)context.GetEndpoint(
            As2Dsl.Receive("/inbound").Host("127.0.0.1").Port(port).ConnectionFactory("me"));
        stats.MessagesIn.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Loopback_EmitsLinkedProducerAndConsumerSpans()
    {
        var spans = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == "redb.Route",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = a => { lock (spans) spans.Add(a); },
        };
        ActivitySource.AddActivityListener(listener);

        var port = FreePort();
        await using var context = new RouteContext();
        context.AddComponent(new As2Component());
        context.AddToRegistry("me", Factory());
        context.AddRoutes(r =>
            r.From(As2Dsl.Receive("/inbound").Host("127.0.0.1").Port(port).ConnectionFactory("me")).Process(_ => { }));
        await context.Start();

        var producer = context.GetEndpoint(As2Dsl.Send($"http://127.0.0.1:{port}/inbound").ConnectionFactory("me")).CreateProducer();
        await producer.Start();
        await producer.Process(new Exchange(new Message("trace~") { ContentType = "application/edi-x12" }));

        var producerSpan = spans.FirstOrDefault(a => a.OperationName == "AS2 POST");
        var consumerSpan = spans.FirstOrDefault(a => a.OperationName == "AS2 receive");
        producerSpan.Should().NotBeNull();
        consumerSpan.Should().NotBeNull();
        producerSpan!.Kind.Should().Be(ActivityKind.Client);
        consumerSpan!.Kind.Should().Be(ActivityKind.Consumer);
        // Distributed trace: the receive span links to the send span's trace via the injected traceparent.
        consumerSpan.TraceId.Should().Be(producerSpan.TraceId);
    }

    [Fact]
    public async Task Loopback_HostResolvedCaller_ReachesTheRoute()
    {
        var port = FreePort();
        await using var manager = new redb.Route.Http.SharedHttpServerManager(new redb.Route.Http.HttpHostingOptions
        {
            // Every request is the one partner here: the resolver's own logic is not what this test is about.
            ResolvePrincipal = _ => Task.FromResult<System.Security.Claims.ClaimsPrincipal?>(
                new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
                    [new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.NameIdentifier, "partner-7")], "test"))),
        });
        await using var context = new RouteContext();
        context.AddComponent(new As2Component { ServerManager = manager });
        context.AddToRegistry("me", Factory());

        var callers = new List<string?>();
        context.AddRoutes(r =>
            r.From(As2Dsl.Receive("/inbound").Host("127.0.0.1").Port(port).ConnectionFactory("me"))
                .Process(e => callers.Add(
                    ExchangePrincipal.Get(e)?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value)));
        await context.Start();

        var producer = context.GetEndpoint(As2Dsl.Send($"http://127.0.0.1:{port}/inbound").ConnectionFactory("me")).CreateProducer();
        await producer.Start();
        await producer.Process(new Exchange(new Message("PO*WHO~") { ContentType = "application/edi-x12" }));

        callers.Should().ContainSingle().Which.Should().Be("partner-7");
    }

    [Fact]
    public async Task AsyncMdn_HostResolvedCaller_ReachesTheReceiptRoute()
    {
        var port = FreePort();
        await using var manager = new redb.Route.Http.SharedHttpServerManager(new redb.Route.Http.HttpHostingOptions
        {
            // Only the receipt request is identified, so a pass proves the MDN receiver's own line rather
            // than the message consumer's (that one is Loopback_HostResolvedCaller_ReachesTheRoute).
            ResolvePrincipal = http => Task.FromResult(http.Request.Path.StartsWithSegments("/mdn")
                ? new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
                    [new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.NameIdentifier, "mdn-sender")], "test"))
                : null),
        });
        await using var context = new RouteContext();
        context.AddComponent(new As2Component { ServerManager = manager });
        context.AddToRegistry("me", new As2ConnectionFactory
        {
            OurCertificate = Cert, PartnerCertificate = Cert, As2From = "US", As2To = "THEM",
            Sign = true, Encrypt = true, SignedMdn = true,
            MdnMode = As2MdnMode.Async, AsyncMdnUrl = $"http://127.0.0.1:{port}/mdn",
        });

        var messageCallers = new System.Collections.Concurrent.ConcurrentQueue<string?>();
        var receiptCallers = new System.Collections.Concurrent.ConcurrentQueue<string?>();
        context.AddRoutes(r =>
        {
            r.From(As2Dsl.Receive("/inbound").Host("127.0.0.1").Port(port).ConnectionFactory("me"))
                .Process(e => messageCallers.Enqueue(CallerId(e)));
            r.From(As2Dsl.ReceiveMdn("/mdn").Host("127.0.0.1").Port(port).ConnectionFactory("me"))
                .Process(e => receiptCallers.Enqueue(CallerId(e)));
        });
        await context.Start();

        var producer = context.GetEndpoint(As2Dsl.Send($"http://127.0.0.1:{port}/inbound").ConnectionFactory("me")).CreateProducer();
        await producer.Start();
        await producer.Process(new Exchange(new Message("PO*RECEIPT~") { ContentType = "application/edi-x12" }));

        // The receiver completes the producer's wait before it hands the receipt to the route, so the
        // route may run a moment after Process returns.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (receiptCallers.IsEmpty && DateTime.UtcNow < deadline)
            await Task.Delay(20);

        messageCallers.Should().ContainSingle().Which.Should().BeNull("only the receipt request is identified here");
        receiptCallers.Should().ContainSingle().Which.Should().Be("mdn-sender");
    }

    [Fact]
    public async Task UnsignedMessage_InPartnershipWithoutRequiredSignature_IsNotReportedAsVerified()
    {
        // The README promises redbAs2.signatureValid means "the inbound signature verified against the
        // partner cert". An unsigned message was not verified at all; reporting true lets a route that
        // gates on the header accept it as authenticated.
        var port = FreePort();
        await using var context = new RouteContext();
        context.AddComponent(new As2Component());
        context.AddToRegistry("plain", new As2ConnectionFactory
        {
            OurCertificate = Cert, PartnerCertificate = Cert, As2From = "US", As2To = "THEM",
            Sign = false, Encrypt = false, MdnMode = As2MdnMode.Sync, SignedMdn = false,
        });

        var reported = new System.Collections.Concurrent.ConcurrentQueue<bool>();
        context.AddRoutes(r =>
            r.From(As2Dsl.Receive("/in").Host("127.0.0.1").Port(port).ConnectionFactory("plain"))
                .Process(e => reported.Enqueue(e.In.GetHeader<bool>(As2Headers.SignatureValid))));
        await context.Start();

        var producer = context.GetEndpoint(As2Dsl.Send($"http://127.0.0.1:{port}/in").ConnectionFactory("plain")).CreateProducer();
        await producer.Start();
        await producer.Process(new Exchange(new Message("PO*PLAIN~") { ContentType = "application/edi-x12" }));

        reported.Should().ContainSingle().Which.Should().BeFalse("nothing was verified: the message carried no signature");
    }

    [Fact]
    public async Task SignedMessage_WithVerifiedSignature_IsReportedAsVerified()
    {
        var port = FreePort();
        await using var context = new RouteContext();
        context.AddComponent(new As2Component());
        context.AddToRegistry("me", Factory());

        var reported = new System.Collections.Concurrent.ConcurrentQueue<bool>();
        context.AddRoutes(r =>
            r.From(As2Dsl.Receive("/inbound").Host("127.0.0.1").Port(port).ConnectionFactory("me"))
                .Process(e => reported.Enqueue(e.In.GetHeader<bool>(As2Headers.SignatureValid))));
        await context.Start();

        var producer = context.GetEndpoint(As2Dsl.Send($"http://127.0.0.1:{port}/inbound").ConnectionFactory("me")).CreateProducer();
        await producer.Start();
        await producer.Process(new Exchange(new Message("PO*SIGNED~") { ContentType = "application/edi-x12" }));

        reported.Should().ContainSingle().Which.Should().BeTrue("the signature was present and verified against the partner certificate");
    }

    private static string? CallerId(IExchange e) =>
        ExchangePrincipal.Get(e)?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;

    private static As2ConnectionFactory Factory() => new()
    {
        OurCertificate = Cert,
        PartnerCertificate = Cert,
        As2From = "US", As2To = "THEM",
        Sign = true, Encrypt = true, SignedMdn = true, MdnMode = As2MdnMode.Sync,
    };

    private static (string contentType, string transferEncoding, byte[] body) BuildAs2Message(string payload)
    {
        var engine = new As2CryptoEngine();
        var part = new MimePart("application", "edi-x12")
        {
            Content = new MimeContent(new MemoryStream(Encoding.UTF8.GetBytes(payload))),
            ContentTransferEncoding = ContentEncoding.Binary,
        };
        var signed = engine.Sign(part, Cert, "sha-256");
        var encrypted = engine.Encrypt(signed, Cert, "aes-128-cbc");

        using var ms = new MemoryStream();
        var options = FormatOptions.Default.Clone();
        options.NewLineFormat = NewLineFormat.Dos;
        encrypted.WriteTo(options, ms);
        var all = ms.ToArray();

        var sep = -1;
        for (var i = 0; i + 3 < all.Length; i++)
            if (all[i] == 13 && all[i + 1] == 10 && all[i + 2] == 13 && all[i + 3] == 10) { sep = i; break; }
        var body = sep >= 0 ? all[(sep + 4)..] : all;

        return (encrypted.Headers[HeaderId.ContentType]!, encrypted.Headers[HeaderId.ContentTransferEncoding]!, body);
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private static X509Certificate2 MakeCert(string cn)
    {
        var rsa = RSA.Create(2048);
        var req = new CertificateRequest($"CN={cn}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certWithKey = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(10));
#pragma warning disable SYSLIB0057
        using var publicOnly = new X509Certificate2(certWithKey.Export(X509ContentType.Cert));
#pragma warning restore SYSLIB0057
        return publicOnly.CopyWithPrivateKey(rsa);
    }
}

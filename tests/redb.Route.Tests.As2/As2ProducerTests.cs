using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using FluentAssertions;
using MimeKit;
using MimeKit.Cryptography;
using redb.Route.Abstractions;
using redb.Route.As2;
using redb.Route.As2.Crypto;
using redb.Route.Core;
using As2Dsl = redb.Route.As2.Fluent.As2;

namespace redb.Route.Tests.As2;

/// <summary>
/// Ф2 producer tests: the AS2 send side POSTs a well-formed, signed+encrypted S/MIME message with the
/// required AS2 headers. A loopback <see cref="HttpListener"/> captures the request; the body is
/// reconstructed and run back through the crypto engine (decrypt → verify → extract) to prove it is a
/// valid AS2 message on the wire. Interop against real AS2 servers is Ф8.
/// </summary>
public class As2ProducerTests
{
    private static readonly X509Certificate2 OurCert = MakeCert("as2-us");
    private static readonly X509Certificate2 PartnerCert = MakeCert("as2-partner");

    [Fact]
    public async Task Send_PostsSignedEncrypted_WithAs2Headers_AndValidBody()
    {
        var port = FreePort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? contentType = null;
        byte[] bodyBytes = [];

        var capture = Task.Run(async () =>
        {
            var ctx = await listener.GetContextAsync();
            foreach (string key in ctx.Request.Headers)
                headers[key] = ctx.Request.Headers[key]!;
            contentType = ctx.Request.ContentType;
            using var ms = new MemoryStream();
            await ctx.Request.InputStream.CopyToAsync(ms);
            bodyBytes = ms.ToArray();
            ctx.Response.StatusCode = 200;
            ctx.Response.Close();
        });

        await using var context = new RouteContext();
        context.AddComponent(new As2Component());
        context.AddToRegistry("partner", new As2ConnectionFactory
        {
            OurCertificate = OurCert,
            PartnerCertificate = PartnerCert,
            As2From = "US", As2To = "THEM",
            Sign = true, Encrypt = true, SignedMdn = true, MdnMode = As2MdnMode.Sync,
        });

        var endpoint = context.GetEndpoint(As2Dsl.Send($"http://127.0.0.1:{port}/as2").ConnectionFactory("partner"));
        var producer = endpoint.CreateProducer();
        await producer.Start();

        var exchange = new Exchange(new Message("ISA*00*...EDI~") { ContentType = "application/edi-x12" });
        await producer.Process(exchange);

        await capture.WaitAsync(TimeSpan.FromSeconds(5));

        // AS2 headers present and correct.
        headers.Should().ContainKey("AS2-Version").WhoseValue.Should().Be("1.2");
        headers.Should().ContainKey("AS2-From").WhoseValue.Should().Be("US");
        headers.Should().ContainKey("AS2-To").WhoseValue.Should().Be("THEM");
        headers.Should().ContainKey("Message-ID");
        headers.Should().ContainKey("Disposition-Notification-To"); // sync MDN requested
        contentType.Should().Contain("application/pkcs7-mime"); // encrypted (enveloped-data)

        // The producer recorded the outgoing Message-ID + MIC for later MDN correlation.
        exchange.In.Headers.Should().ContainKey(As2Headers.Mic);
        exchange.In.GetHeader<string>(As2Headers.MessageId).Should().StartWith("<");

        // Reconstruct the MIME message from the captured MIME headers + body, then decrypt + verify.
        var engine = new As2CryptoEngine();
        var entity = LoadEntity(headers, bodyBytes);
        var decrypted = engine.Decrypt(entity, PartnerCert);
        decrypted.Should().BeAssignableTo<MultipartSigned>();
        engine.Verify((MultipartSigned)decrypted, OurCert).Should().BeTrue();

        var payload = (MimePart)((MultipartSigned)decrypted)[0];
        using var decoded = new MemoryStream();
        payload.Content.DecodeTo(decoded);
        Encoding.UTF8.GetString(decoded.ToArray()).Should().Be("ISA*00*...EDI~");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static MimeEntity LoadEntity(Dictionary<string, string> headers, byte[] body)
    {
        // Rebuild the MIME entity from the top-level MIME headers the producer promoted to HTTP headers.
        var sb = new StringBuilder();
        sb.Append("Content-Type: ").Append(headers["Content-Type"]).Append("\r\n");
        if (headers.TryGetValue("Content-Transfer-Encoding", out var cte))
            sb.Append("Content-Transfer-Encoding: ").Append(cte).Append("\r\n");
        if (headers.TryGetValue("Content-Disposition", out var cd))
            sb.Append("Content-Disposition: ").Append(cd).Append("\r\n");
        sb.Append("\r\n");

        using var stream = new MemoryStream();
        stream.Write(Encoding.ASCII.GetBytes(sb.ToString()));
        stream.Write(body);
        stream.Position = 0;
        return MimeEntity.Load(stream);
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

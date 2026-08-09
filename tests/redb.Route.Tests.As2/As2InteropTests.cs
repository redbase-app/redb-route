using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using FluentAssertions;
using redb.Route.Abstractions;
using redb.Route.As2;
using redb.Route.Core;
using As2Dsl = redb.Route.As2.Fluent.As2;

namespace redb.Route.Tests.As2;

/// <summary>
/// Ф8 interop tests against a real OpenAS2 server (see <c>C:\Work\yaml\as2</c>). These prove interop
/// correctness — that an external AS2 implementation accepts our signed+encrypted wire format and MIC —
/// which loopback e2e cannot. GATED: each test no-ops if OpenAS2 is not reachable on 127.0.0.1:14080, so
/// the normal suite stays green without the container. Run with <c>--filter Category=Interop</c> after
/// <c>docker compose up</c> in the harness directory.
/// </summary>
[Trait("Category", "Interop")]
public class As2InteropTests
{
    private const string OpenAs2Host = "127.0.0.1";
    private const int OpenAs2Port = 14080;
    private static string CertsDir =>
        Environment.GetEnvironmentVariable("AS2_INTEROP_CERTS") ?? @"C:\Work\yaml\as2\certs";

    [Fact]
    public async Task Redb_To_OpenAs2_SignedEncrypted_ReturnsPositiveMdn()
    {
        if (!IsReachable(OpenAs2Host, OpenAs2Port))
            return; // gated: OpenAS2 container not running — see C:\Work\yaml\as2\README.md

        var ourCert = LoadPkcs12(Path.Combine(CertsDir, "redb.p12"), "testpass");
        var partnerCert = LoadCertificate(Path.Combine(CertsDir, "openas2.crt"));

        await using var context = new RouteContext();
        context.AddComponent(new As2Component());
        context.AddToRegistry("openas2", new As2ConnectionFactory
        {
            OurCertificate = ourCert,
            PartnerCertificate = partnerCert,
            As2From = "redb", As2To = "openas2",
            Sign = true, Encrypt = true, SignAlg = "sha-256", EncryptAlg = "aes-128-cbc",
            SignedMdn = true, MdnMode = As2MdnMode.Sync,
        });
        await context.Start();

        var producer = context.GetEndpoint(
            As2Dsl.Send($"http://{OpenAs2Host}:{OpenAs2Port}/").ConnectionFactory("openas2")).CreateProducer();
        await producer.Start();

        var exchange = new Exchange(new Message("ISA*00*          *00*          *ZZ*REDB~") { ContentType = "application/edi-x12" });
        await producer.Process(exchange);

        // OpenAS2 accepted our signed+encrypted message and returned a positive, verifiable MDN.
        exchange.HasOut.Should().BeTrue();
        exchange.Out!.GetHeader<string>(As2Headers.MdnDisposition).Should().Contain("processed");
        exchange.Out!.GetHeader<bool>(As2Headers.MdnMicMatch).Should().BeTrue();
    }

    [Fact]
    public async Task OpenAs2_To_Redb_ReceivesSignedEncrypted()
    {
        if (!IsReachable(OpenAs2Host, OpenAs2Port))
            return; // gated: OpenAS2 container not running

        const int consumerPort = 15081; // matches partnerships.xml as2_url host.docker.internal:15081
        var ourCert = LoadPkcs12(Path.Combine(CertsDir, "redb.p12"), "testpass");     // our key: decrypt
        var partnerCert = LoadCertificate(Path.Combine(CertsDir, "openas2.crt"));     // partner cert: verify

        await using var context = new RouteContext();
        context.AddComponent(new As2Component());
        context.AddToRegistry("openas2", new As2ConnectionFactory
        {
            OurCertificate = ourCert,
            PartnerCertificate = partnerCert,
            As2From = "redb", As2To = "openas2",
            Sign = true, Encrypt = true, SignedMdn = true, MdnMode = As2MdnMode.Sync,
        });

        var received = new List<string>();
        context.AddRoutes(r =>
            r.From(As2Dsl.Receive("/inbound").Host("0.0.0.0").Port(consumerPort).ConnectionFactory("openas2"))
                .Process(e => received.Add(Encoding.UTF8.GetString((byte[])e.In.Body!))));
        await context.Start();

        // Drop a file into OpenAS2's outbox for partner "redb"; its directory poller picks it up, builds a
        // signed+encrypted AS2 message and POSTs it to our consumer at host.docker.internal:15081/inbound.
        var harnessDir = Path.GetDirectoryName(CertsDir.TrimEnd('\\', '/'))!;
        var outbox = Path.Combine(harnessDir, "data", "outbox", "redb");
        Directory.CreateDirectory(outbox);
        const string payload = "ISA*00*OPENAS2*ZZ*REDB*REVERSE~";
        await File.WriteAllTextAsync(Path.Combine(outbox, $"reverse-{Guid.NewGuid():N}.edi"), payload);

        // Wait for the poller (5s interval) + delivery.
        var deadline = DateTime.UtcNow.AddSeconds(40);
        while (received.Count == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(500);

        received.Should().ContainSingle().Which.Should().Be(payload);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static bool IsReachable(string host, int port)
    {
        try
        {
            using var client = new TcpClient();
            var connect = client.ConnectAsync(host, port);
            return connect.Wait(TimeSpan.FromMilliseconds(500)) && client.Connected;
        }
        catch
        {
            return false;
        }
    }

    private static X509Certificate2 LoadPkcs12(string path, string password)
    {
#pragma warning disable SYSLIB0057 // byte[]/password ctor is obsolete on net9+ but present on net8; suppress across TFMs
        return new X509Certificate2(File.ReadAllBytes(path), password, X509KeyStorageFlags.Exportable);
#pragma warning restore SYSLIB0057
    }

    private static X509Certificate2 LoadCertificate(string path)
        => X509Certificate2.CreateFromPem(File.ReadAllText(path));
}

using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using FluentAssertions;
using redb.Route.Abstractions;
using redb.Route.As2;
using redb.Route.Core;
using Xunit.Sdk;
using As2Dsl = redb.Route.As2.Fluent.As2;

namespace redb.Route.Tests.As2;

/// <summary>
/// A fact that needs the OpenAS2 interop stand (see <c>C:\Work\yaml\as2</c>) on 127.0.0.1:14080. Skipped, visibly,
/// when nothing listens there, unless <c>REDB_AS2_INTEROP=1</c> makes that a failure (the AS4 stand gate).
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
[XunitTestCaseDiscoverer("Xunit.Sdk.FactDiscoverer", "xunit.execution.{Platform}")]
public sealed class As2InteropFactAttribute : FactAttribute
{
    public As2InteropFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("REDB_AS2_INTEROP") != "1" && !As2InteropTests.IsReachable("127.0.0.1", 14080))
            Skip = "OpenAS2 stand is not up on 127.0.0.1:14080 (docker compose up in C:/Work/yaml/as2); REDB_AS2_INTEROP=1 requires it.";
    }
}

/// <summary>
/// Ф8 interop tests against a real OpenAS2 server (see <c>C:\Work\yaml\as2</c>). These prove interop
/// correctness — that an external AS2 implementation accepts our signed+encrypted wire format and MIC —
/// which loopback e2e cannot. Skipped visibly without the stand (<see cref="As2InteropFactAttribute"/>).
/// Run with <c>--filter Category=Interop</c> after <c>docker compose up</c> in the harness directory.
/// </summary>
[Trait("Category", "Interop")]
public class As2InteropTests
{
    private const string OpenAs2Host = "127.0.0.1";
    private const int OpenAs2Port = 14080;
    private static string CertsDir =>
        Environment.GetEnvironmentVariable("AS2_INTEROP_CERTS") ?? @"C:\Work\yaml\as2\certs";

    [As2InteropFact]
    public async Task Redb_To_OpenAs2_AsyncMdn_IsPostedBack_Signed_AndConfirms()
    {
        // OpenAS2 answers 200 at once and posts its signed MDN to the Receipt-Delivery-Option we send: our ReceiveMdn
        // endpoint on the host, reached from the container as host.docker.internal.
        var ourCert = LoadPkcs12(Path.Combine(CertsDir, "redb.p12"), "testpass");
        var partnerCert = LoadCertificate(Path.Combine(CertsDir, "openas2.crt"));
        var mdnPort = global::redb.Route.Tests.Shared.TestPorts.Next();

        await using var context = new RouteContext();
        context.AddComponent(new As2Component());
        context.AddToRegistry("openas2", new As2ConnectionFactory
        {
            OurCertificate = ourCert,
            PartnerCertificate = partnerCert,
            As2From = "redb", As2To = "openas2",
            Sign = true, Encrypt = true, SignAlg = "sha-256", EncryptAlg = "aes-128-cbc",
            SignedMdn = true, RequireValidMdn = true,
            MdnMode = As2MdnMode.Async, AsyncMdnUrl = $"http://host.docker.internal:{mdnPort}/as2/mdn",
        });
        var receipts = new System.Collections.Concurrent.ConcurrentQueue<(string? Id, bool Confirmed, bool Signed, string? Mic)>();
        context.AddRoutes(r => r.From(As2Dsl.ReceiveMdn("/as2/mdn").Host("0.0.0.0").Port(mdnPort).ConnectionFactory("openas2"))
            .Process(e => receipts.Enqueue((
                e.In.GetHeader<string>(As2Headers.MessageId),
                e.In.GetHeader<bool>(As2Headers.MdnConfirmed),
                e.In.GetHeader<bool>(As2Headers.SignatureValid),
                e.In.GetHeader<string>(As2Headers.MdnMicStatus)))));
        await context.Start();

        var producer = context.GetEndpoint(
            As2Dsl.Send($"http://{OpenAs2Host}:{OpenAs2Port}/").ConnectionFactory("openas2")).CreateProducer();
        await producer.Start();
        var exchange = new Exchange(new Message($"ISA*00*ASYNC*{Guid.NewGuid():N}~") { ContentType = "application/edi-x12" });
        await producer.Process(exchange);
        var sentId = exchange.In.GetHeader<string>(As2Headers.MessageId);

        exchange.HasOut.Should().BeFalse("an asynchronous MDN is not in the response");
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!receipts.Any(r => r.Id == sentId) && DateTime.UtcNow < deadline)
            await Task.Delay(250);

        receipts.Should().Contain(r => r.Id == sentId, "OpenAS2 posts the MDN to our Receipt-Delivery-Option")
            .Which.Should().Be((sentId, true, true, "matched"));
    }

    [As2InteropFact]
    public async Task OpenAs2_To_Redb_AsyncMdn_IsPostedToOpenAs2_AndItClosesThePendingMessage()
    {
        // Partnership openas2-to-redb-async asks for an asynchronous receipt at http://127.0.0.1:14081 (OpenAS2's MDN
        // receiver as the host reaches it). Our receiver answers 200 and posts the signed MDN there; OpenAS2 matches it
        // to its pending message and deletes the pending file.
        const int consumerPort = 15082; // matches the partnership's as2_url host.docker.internal:15082
        var ourCert = LoadPkcs12(Path.Combine(CertsDir, "redb.p12"), "testpass");
        var partnerCert = LoadCertificate(Path.Combine(CertsDir, "openas2.crt"));

        await using var standLock = await LockAsync(consumerPort);
        await using var context = new RouteContext();
        context.AddComponent(new As2Component());
        context.AddToRegistry("openas2", new As2ConnectionFactory
        {
            OurCertificate = ourCert,
            PartnerCertificate = partnerCert,
            As2From = "redb-async", As2To = "openas2",
            Sign = true, Encrypt = true, SignedMdn = true,
            MdnMode = As2MdnMode.Async, AsyncMdnAllowedHosts = ["127.0.0.1"],
        });
        var received = new List<string>();
        context.AddRoutes(r =>
            r.From(As2Dsl.Receive("/inbound").Host("0.0.0.0").Port(consumerPort).ConnectionFactory("openas2"))
                .Process(e => { lock (received) received.Add(Encoding.UTF8.GetString((byte[])e.In.Body!)); }));
        await context.Start();

        var harnessDir = Path.GetDirectoryName(CertsDir.TrimEnd('\\', '/'))!;
        var outbox = Path.Combine(harnessDir, "data", "outbox", "redb-async");
        var pending = Path.Combine(harnessDir, "data", "pendinginfoMDN3");
        Directory.CreateDirectory(outbox);
        var name = $"async-{Guid.NewGuid():N}";
        var payload = $"ISA*00*OPENAS2*ZZ*REDB*ASYNC*{name}~";
        await File.WriteAllTextAsync(Path.Combine(outbox, name + ".edi"), payload);

        bool Seen() { lock (received) return received.Contains(payload); }
        bool Pending() => Directory.Exists(pending) && Directory.EnumerateFiles(pending).Any(f => f.Contains(name, StringComparison.Ordinal));

        var deadline = DateTime.UtcNow.AddSeconds(40);
        while (!Seen() && DateTime.UtcNow < deadline)
            await Task.Delay(250);
        Seen().Should().BeTrue("OpenAS2 delivers the dropped file to our receiver");

        deadline = DateTime.UtcNow.AddSeconds(20);
        while (Pending() && DateTime.UtcNow < deadline)
            await Task.Delay(250);
        Pending().Should().BeFalse("OpenAS2 accepted our asynchronous MDN and closed the pending message");
    }

    /// <summary>
    /// A file opened without sharing is the lock between the three target frameworks' processes on a port the stand
    /// posts to; released by the process when it ends, whatever thread it is on.
    /// </summary>
    private static async Task<FileStream> LockAsync(int port)
    {
        var lockPath = Path.Combine(Path.GetTempPath(), $"redb-as2-interop-{port}.lock");
        var deadline = DateTime.UtcNow.AddMinutes(3);
        while (true)
        {
            try { return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (DateTime.UtcNow < deadline) { await Task.Delay(250); }
        }
    }

    [As2InteropFact]
    public async Task Redb_To_OpenAs2_SignedEncrypted_ReturnsPositiveMdn()
    {

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

    [As2InteropFact]
    public async Task OpenAs2_To_Redb_ReceivesSignedEncrypted()
    {
        // The stand posts to one fixed port, and the three target frameworks run as three processes at once: without
        // the lock one of them holds the port and receives the others' documents while they time out.
        // A file opened without sharing is the lock: released by the process when it ends, whatever thread it is on.
        var lockPath = Path.Combine(Path.GetTempPath(), "redb-as2-interop-15081.lock");
        FileStream? standLock = null;
        var deadline = DateTime.UtcNow.AddMinutes(3);
        while (standLock is null)
        {
            try { standLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (DateTime.UtcNow < deadline) { await Task.Delay(250); }
        }
        await using (standLock)
            await ReverseAsync();
    }

    private static async Task ReverseAsync()
    {
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
                .Process(e => { lock (received) received.Add(Encoding.UTF8.GetString((byte[])e.In.Body!)); }));
        await context.Start();

        // Drop a file into OpenAS2's outbox for partner "redb"; its directory poller picks it up, builds a
        // signed+encrypted AS2 message and POSTs it to our consumer at host.docker.internal:15081/inbound.
        var harnessDir = Path.GetDirectoryName(CertsDir.TrimEnd('\\', '/'))!;
        var outbox = Path.Combine(harnessDir, "data", "outbox", "redb");
        Directory.CreateDirectory(outbox);
        var payload = $"ISA*00*OPENAS2*ZZ*REDB*REVERSE*{Guid.NewGuid():N}~";
        await File.WriteAllTextAsync(Path.Combine(outbox, $"reverse-{Guid.NewGuid():N}.edi"), payload);

        // Wait for the poller (5s interval) + delivery.
        var deadline = DateTime.UtcNow.AddSeconds(40);
        while (!Seen() && DateTime.UtcNow < deadline)
            await Task.Delay(500);

        Seen().Should().BeTrue("OpenAS2 delivers the dropped file to our receiver");
        bool Seen() { lock (received) return received.Contains(payload); }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    internal static bool IsReachable(string host, int port)
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

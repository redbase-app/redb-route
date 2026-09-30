// ============================================================================
//  As4Worker — a debug host for the As4Module Tsak module.
//
//  What the Tsak worker does, in the smallest possible way:
//    1) prepare the demo folder: a self-signed certificate (DEMO ONLY) and outbox/, inbox/;
//    2) hand a RouteContext to As4Module.InitRoute.main — the SAME entry point the worker calls;
//    3) drop one sample document into outbox/, so the first round trip happens by itself.
//
//  Then drop any file into as4-demo/outbox: it is signed, encrypted and sent over AS4, the receiver verifies
//  it and writes it to as4-demo/inbox, and the signed receipt is logged.
// ============================================================================

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using redb.Route.Core;                    // RouteContext

namespace As4Worker;

public static class Program
{
    public static async Task Main(string[] args)
    {
        // ── The demo folder next to the exe (not the current directory: that depends on where you start it) ──
        var dir = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.Combine(AppContext.BaseDirectory, "as4-demo");
        foreach (var sub in new[] { "certs", "outbox", "sent", "failed", "inbox" })
            Directory.CreateDirectory(Path.Combine(dir, sub));
        EnsureDemoCertificate(Path.Combine(dir, "certs", "node.pfx"));
        Environment.SetEnvironmentVariable("AS4_DEMO_DIR", dir);

        // ── DI: console logging ──
        var services = new ServiceCollection();
        services.AddLogging(b => b
            .AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; })
            .SetMinimumLevel(LogLevel.Information));
        var sp = services.BuildServiceProvider();

        // ── Build a route context and call the module entry point ──
        var ctx = new RouteContext(sp, contextId: "as4-worker");
        ctx.AddService(typeof(ILoggerFactory), sp.GetRequiredService<ILoggerFactory>());
        As4Module.InitRoute.main(ctx);        // <- the exact method the Tsak worker calls

        await ctx.Start();

        // ── One sample document, so the first round trip happens by itself ──
        var sample = Path.Combine(dir, "outbox", $"invoice-{DateTime.Now:HHmmss}.xml");
        await File.WriteAllTextAsync(sample, "<Invoice><Id>INV-001</Id><Amount currency=\"EUR\">120.00</Amount></Invoice>");

        Console.WriteLine();
        Console.WriteLine($"As4Worker running. AS4 receiver: http://localhost:{As4Module.InitRoute.Port}/as4/in");
        Console.WriteLine($"  drop files into  {Path.Combine(dir, "outbox")}");
        Console.WriteLine($"  received land in {Path.Combine(dir, "inbox")}");
        Console.WriteLine($"  sent are moved to {Path.Combine(dir, "sent")}, undelivered moved to {Path.Combine(dir, "failed")}");
        Console.WriteLine("Ctrl+C to exit.");
        Console.WriteLine();

        var stop = new ManualResetEventSlim();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Set(); };
        stop.Wait();

        await ctx.DisposeAsync();
    }

    // DEMO ONLY: a self-signed RSA certificate that signs and decrypts for both sides of the loopback. A real node holds
    // the certificate its PKI (or the network's, e.g. PEPPOL) issued, and the partner's certificate is the partner's.
    private static void EnsureDemoCertificate(string path)
    {
        if (File.Exists(path))
            return;
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=redb-demo AS4 node", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, critical: false));
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(2));
        File.WriteAllBytes(path, certificate.Export(X509ContentType.Pfx, As4Module.InitRoute.PfxPassword));
        Console.WriteLine($"Generated a self-signed demo certificate: {path}");
    }
}

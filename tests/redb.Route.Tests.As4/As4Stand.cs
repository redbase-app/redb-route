using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Xunit.Sdk;

namespace redb.Route.Tests.As4;

/// <summary>
/// The AS4 interop harness (<c>C:\Work\yaml\as4</c>, or <c>AS4_STAND</c>): Holodeck nodes, test keys and
/// captured reference messages. No key material lives in the repository.
/// </summary>
internal static class As4Stand
{
    /// <summary>Root of the harness.</summary>
    public static string Root => Environment.GetEnvironmentVariable("AS4_STAND") ?? @"C:\Work\yaml\as4";

    /// <summary>True when the run must not skip harness tests (<c>REDB_AS4_INTEROP=1</c>).</summary>
    public static bool Required => Environment.GetEnvironmentVariable("REDB_AS4_INTEROP") == "1";

    /// <summary>A captured exchange: body bytes and the HTTP Content-Type it came with.</summary>
    public static (byte[] Body, string ContentType) Capture(string name, string side)
    {
        var dir = Path.Combine(Root, "captures", name);
        var body = File.ReadAllBytes(Path.Combine(dir, side + ".bin"));
        using var meta = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, side + ".json")));
        var contentType = meta.RootElement.GetProperty("headers").GetProperty("content-type").GetString()!;
        return (body, contentType);
    }

    /// <summary>A file of a capture folder.</summary>
    public static byte[] CaptureFile(string name, string file) => File.ReadAllBytes(Path.Combine(Root, "captures", name, file));

    /// <summary>A test key pair of the harness (<c>keys/&lt;name&gt;.p12</c>, password <c>testpass</c>).</summary>
    public static X509Certificate2 KeyPair(string name)
    {
        var path = Path.Combine(Root, "keys", name + ".p12");
#if NET9_0_OR_GREATER
        return X509CertificateLoader.LoadPkcs12FromFile(path, "testpass", X509KeyStorageFlags.Exportable);
#else
        return new X509Certificate2(path, "testpass", X509KeyStorageFlags.Exportable);
#endif
    }

    /// <summary>
    /// A lock shared by every test process on this machine — the three target frameworks run in parallel, and a
    /// test that Holodeck calls back on a fixed port can run in one of them at a time. A lock file rather than a
    /// named mutex: it is not bound to the acquiring thread, which an async test does not keep.
    /// </summary>
    public static async Task<IAsyncDisposable> ExclusiveAsync(string name, TimeSpan timeout)
    {
        var path = Path.Combine(Path.GetTempPath(), $"redb-as4-{name}.lock");
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250));   // held by another test process
            }
        }
    }

    /// <summary>
    /// Reads a file the stand may still be writing: shared with the writer, retried while the writer holds it exclusively
    /// (Holodeck writes its msg_in files in place).
    /// </summary>
    public static string ReadShared(string path)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                return reader.ReadToEnd();
            }
            catch (IOException) when (attempt < 20)
            {
                Thread.Sleep(100);
            }
        }
    }

    /// <summary>Whether something accepts TCP connections on 127.0.0.1:<paramref name="port"/>.</summary>
    public static bool Listening(int port)
    {
        using var client = new System.Net.Sockets.TcpClient();
        try
        {
            return client.ConnectAsync("127.0.0.1", port).Wait(TimeSpan.FromSeconds(1)) && client.Connected;
        }
        catch (AggregateException e) when (e.InnerException is System.Net.Sockets.SocketException)
        {
            return false;   // refused: that is the answer "nothing listens"
        }
    }

    /// <summary>A test certificate of the harness without its key (<c>keys/&lt;name&gt;.crt</c>).</summary>
    public static X509Certificate2 Certificate(string name)
    {
        var path = Path.Combine(Root, "keys", name + ".crt");
#if NET9_0_OR_GREATER
        return X509CertificateLoader.LoadCertificateFromFile(path);
#else
        return new X509Certificate2(path);
#endif
    }
}

/// <summary>
/// A fact that needs the AS4 harness files. Without them it is skipped — visibly, not passed — unless
/// <c>REDB_AS4_INTEROP=1</c>, which makes a missing harness a failure.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
[XunitTestCaseDiscoverer("Xunit.Sdk.FactDiscoverer", "xunit.execution.{Platform}")]
public sealed class As4StandFactAttribute : FactAttribute
{
    /// <summary>Skips when <paramref name="relativePath"/> is not present under the harness root.</summary>
    public As4StandFactAttribute(string relativePath)
    {
        if (!As4Stand.Required && !Path.Exists(Path.Combine(As4Stand.Root, relativePath)))
            Skip = $"AS4 harness not found ({Path.Combine(As4Stand.Root, relativePath)}); set AS4_STAND, or REDB_AS4_INTEROP=1 to require it.";
    }
}

/// <summary>
/// A fact that needs a live harness endpoint on <c>127.0.0.1:port</c> (a Holodeck node or the recorder).
/// Skipped when it does not answer — visibly — unless <c>REDB_AS4_INTEROP=1</c> makes that a failure.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
[XunitTestCaseDiscoverer("Xunit.Sdk.FactDiscoverer", "xunit.execution.{Platform}")]
public sealed class As4LiveFactAttribute : FactAttribute
{
    /// <summary>Skips when nothing listens on <paramref name="port"/>.</summary>
    public As4LiveFactAttribute(int port)
    {
        if (!As4Stand.Required && !As4Stand.Listening(port))
            Skip = $"AS4 harness endpoint 127.0.0.1:{port} is not up (docker compose --profile reference up -d in {As4Stand.Root}); REDB_AS4_INTEROP=1 requires it.";
    }
}

/// <summary>The theory form of <see cref="As4LiveFactAttribute"/>.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
[XunitTestCaseDiscoverer("Xunit.Sdk.TheoryDiscoverer", "xunit.execution.{Platform}")]
public sealed class As4LiveTheoryAttribute : TheoryAttribute
{
    /// <summary>Skips when nothing listens on <paramref name="port"/>.</summary>
    public As4LiveTheoryAttribute(int port)
    {
        if (!As4Stand.Required && !As4Stand.Listening(port))
            Skip = $"AS4 harness endpoint 127.0.0.1:{port} is not up (docker compose --profile reference up -d in {As4Stand.Root}); REDB_AS4_INTEROP=1 requires it.";
    }
}

using System.Diagnostics;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Xml;
using FluentAssertions;
using redb.Route.Soap;

namespace redb.Route.Tests.Soap;

/// <summary>
/// Ф8 XML-Encryption interop: proves the WSS layout we emit (EncryptedKey in the Security header, AES body in
/// EncryptedData, joined by a ReferenceList) is decryptable by an INDEPENDENT crypto stack — Node.js built-in
/// <c>crypto</c> (OpenSSL), with zero shared code and no XML-Encryption library. It RSA-OAEP-unwraps the
/// session key and AES-256-CBC-decrypts the body straight from our envelope. GATED (<c>Category=Interop</c>):
/// no-ops unless the Node container answers on 127.0.0.1:18080. Harness in <c>C:\Work\yaml\soap</c>.
/// </summary>
[Trait("Category", "Interop")]
public class SoapEncryptionInteropTests
{
    private const string Host = "127.0.0.1";
    private const int ServerPort = 18080;
    private static string StandDir =>
        Environment.GetEnvironmentVariable("SOAP_INTEROP_DIR") ?? @"C:\Work\yaml\soap";

    [Fact]
    public void RedbWssEncryption_DecryptedBy_IndependentNodeCrypto()
    {
        if (!IsReachable(Host, ServerPort) || !Directory.Exists(StandDir))
            return; // gated: Node container / stand not present

        // Encrypt to the cert's public key; keep the matching RSA private key to hand Node for the decrypt.
        using var rsa = RSA.Create(2048);
        var req = new System.Security.Cryptography.X509Certificates.CertificateRequest(
            "CN=soap-enc", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));

        var envelope = SoapEnvelope.Build("<Secret xmlns=\"urn:test\"><n>4242</n></Secret>", SoapVersion.Soap11);
        var doc = new XmlDocument { PreserveWhitespace = true };
        doc.Load(new MemoryStream(envelope));
        SoapEncryption.EncryptBody(doc, cert, SoapVersion.Soap11);

        // Per-runtime file names so the parallel net8/net9/net10 hosts don't race on the same files.
        var tag = Environment.Version.Major;
        File.WriteAllText(Path.Combine(StandDir, $"enc-envelope-{tag}.xml"), doc.OuterXml);
        File.WriteAllText(Path.Combine(StandDir, $"enc-key-{tag}.pem"), rsa.ExportPkcs8PrivateKeyPem());

        var (stdout, stderr, exit) = RunNode(
            $"ENC_XML=/app/enc-envelope-{tag}.xml ENC_PEM=/app/enc-key-{tag}.pem node /app/xmlenc-verify.js");

        exit.Should().Be(0, $"independent Node crypto should decrypt our WSS envelope; stderr='{stderr}'");
        stdout.Should().Contain("PLAIN:").And.Contain("Secret").And.Contain("4242");
    }

    private static (string stdout, string stderr, int exit) RunNode(string command)
    {
        var psi = new ProcessStartInfo("docker")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("exec");
        psi.ArgumentList.Add("soap-echo");
        psi.ArgumentList.Add("sh");
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(command);

        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit(15000);
        return (stdout, stderr, p.HasExited ? p.ExitCode : -1);
    }

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
}

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace redb.Route.Tests.RabbitMQ;

/// <summary>Self-signed client certificates made on the spot, with the validity period a test needs.</summary>
internal static class TestCertificates
{
    public const string Passphrase = "test";

    /// <summary>A certificate with its private key, valid from <paramref name="notBefore"/> to <paramref name="notAfter"/>.</summary>
    public static X509Certificate2 Client(DateTimeOffset notBefore, DateTimeOffset notAfter)
    {
        // Through PFX so the private key is usable on every platform (ephemeral keys are not on Windows TLS).
        var pfx = PfxBytes(notBefore, notAfter);
#if NET9_0_OR_GREATER
        return X509CertificateLoader.LoadPkcs12(pfx, Passphrase);
#else
        return new X509Certificate2(pfx, Passphrase);
#endif
    }

    /// <summary>The same, written to a temporary PFX file that is deleted on dispose.</summary>
    public static TempFile ClientPfx(DateTimeOffset notBefore, DateTimeOffset notAfter)
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"redb-rmq-{Guid.NewGuid():N}.pfx");
        File.WriteAllBytes(path, PfxBytes(notBefore, notAfter));
        return new TempFile(path);
    }

    private static byte[] PfxBytes(DateTimeOffset notBefore, DateTimeOffset notAfter)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=redb-test-client", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var created = request.CreateSelfSigned(notBefore, notAfter);
        return created.Export(X509ContentType.Pfx, Passphrase);
    }

    public sealed record TempFile(string Path) : IDisposable
    {
        public void Dispose() => File.Delete(Path);
    }
}

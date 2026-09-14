using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace SerialNumbers.Worker;

/// <summary>
/// Self-signed AS2 certificates for the demo: one pair for the hub, one for the simulated partner
/// GLOBEX. Each side keeps its private key (<c>.pfx</c>) and hands the other side its public
/// certificate (<c>.cer</c>). Real partners exchange certificates issued for the partnership.
/// </summary>
public static class DemoCertificates
{
    public static void EnsureCreated(string directory, string password)
    {
        Directory.CreateDirectory(directory);
        EnsurePair(directory, "hub", "SERIALS-HUB", password);
        EnsurePair(directory, "globex", "GLOBEX", password);
    }

    private static void EnsurePair(string directory, string name, string commonName, string password)
    {
        var pfxPath = Path.Combine(directory, $"{name}.pfx");
        var cerPath = Path.Combine(directory, $"{name}.cer");
        if (File.Exists(pfxPath) && File.Exists(cerPath))
            return;

        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest($"CN={commonName}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(5));

        File.WriteAllBytes(pfxPath, certificate.Export(X509ContentType.Pkcs12, password));
        File.WriteAllBytes(cerPath, certificate.Export(X509ContentType.Cert));
    }
}

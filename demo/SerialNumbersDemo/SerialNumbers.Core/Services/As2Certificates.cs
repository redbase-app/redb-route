using System.Security.Cryptography.X509Certificates;

namespace SerialNumbers.Core.Services;

/// <summary>
/// AS2 key material from a directory: <c>hub.pfx</c> is the hub's own certificate with its private
/// key (signs what we send, decrypts what we receive), <c>{partner}.cer</c> is each partner's
/// public certificate (encrypts what we send, verifies what we receive).
/// </summary>
public static class As2Certificates
{
    public static X509Certificate2 LoadHub(string directory, string password) =>
        X509CertificateLoader.LoadPkcs12FromFile(
            Path.Combine(directory, "hub.pfx"), password, X509KeyStorageFlags.Exportable);

    public static X509Certificate2 LoadPartner(string directory, string partnerCode) =>
        X509CertificateLoader.LoadCertificateFromFile(Path.Combine(directory, $"{partnerCode}.cer"));
}

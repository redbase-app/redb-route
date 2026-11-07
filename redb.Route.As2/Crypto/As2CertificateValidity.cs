using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace redb.Route.As2.Crypto;

/// <summary>
/// Whether a certificate an agreement pins may be used now. The pin says whose key it is; it does not make an expired
/// key current (the AS4 connector's rule, after Holodeck and Domibus). Revocation (CRL/OCSP) and chain trust are not
/// checked: the pin is the trust, and revocation is a documented gap of this connector.
/// </summary>
internal static class As2CertificateValidity
{
    /// <summary>Null when <paramref name="certificate"/> is valid at <paramref name="now"/>; otherwise why not, naming <paramref name="role"/>.</summary>
    public static string? Problem(X509Certificate2 certificate, DateTimeOffset now, string role)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        var who = $"{role} {certificate.Subject} ({certificate.Thumbprint})";
        var utc = now.UtcDateTime;
        if (utc < certificate.NotBefore.ToUniversalTime())
            return $"{who} is not yet valid: valid from {certificate.NotBefore.ToUniversalTime():O}.";
        if (utc > certificate.NotAfter.ToUniversalTime())
            return $"{who} has expired: valid until {certificate.NotAfter.ToUniversalTime():O}.";
        return null;
    }

    /// <summary>Throws <see cref="CryptographicException"/> when <paramref name="certificate"/> may not be used now.</summary>
    public static void Ensure(X509Certificate2 certificate, string role)
    {
        if (Problem(certificate, DateTimeOffset.UtcNow, role) is { } problem)
            throw new CryptographicException(problem);
    }
}

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace redb.Route.As4.Security;

/// <summary>
/// How the revocation of pinned certificates is checked (<see cref="As4ConnectionFactory.RevocationMode"/>).
/// </summary>
/// <param name="Mode">
/// <see cref="X509RevocationMode.NoCheck"/> (default), <see cref="X509RevocationMode.Online"/> (fetch CRLs and OCSP
/// responses the certificate points at) or <see cref="X509RevocationMode.Offline"/> (the OS cache only).
/// </param>
/// <param name="SoftFail">Accept a certificate whose status cannot be determined (the responder is unreachable). Default false.</param>
internal sealed record RevocationPolicy(X509RevocationMode Mode, bool SoftFail)
{
    /// <summary>No revocation check.</summary>
    public static readonly RevocationPolicy None = new(X509RevocationMode.NoCheck, SoftFail: false);
}

/// <summary>
/// Whether a certificate an agreement pins may be used now. Pinning says whose key it is; it does not make an expired
/// or revoked key current — Holodeck ("directly trusted, check validity") and Domibus check both on top of the pin
/// (<c>domibus.sender.certificate.validation.onreceiving</c>, <c>domibus.sender/receiver.certificate.validation.onsending</c>).
/// Revocation follows phase4: off unless asked for, and then hard-fail when the status cannot be determined unless
/// soft-fail is chosen; a certificate that names no CRL distribution point and no authority information access passes,
/// as in Domibus. Trust in the chain is not asked for: the pin is the trust.
/// </summary>
internal static class CertificateValidity
{
    private const string CrlDistributionPointsOid = "2.5.29.31";
    private const string AuthorityInformationAccessOid = "1.3.6.1.5.5.7.1.1";

    /// <summary>Null when <paramref name="certificate"/> is valid at <paramref name="now"/>; otherwise why not, naming <paramref name="role"/>.</summary>
    public static string? Problem(X509Certificate2 certificate, DateTimeOffset now, string role) =>
        Problem(certificate, now, role, RevocationPolicy.None);

    /// <summary>The validity period, then revocation under <paramref name="revocation"/>.</summary>
    public static string? Problem(X509Certificate2 certificate, DateTimeOffset now, string role, RevocationPolicy revocation)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        ArgumentNullException.ThrowIfNull(revocation);
        var who = $"{role} {certificate.Subject} ({certificate.Thumbprint})";

        var utc = now.UtcDateTime;
        if (utc < certificate.NotBefore.ToUniversalTime())
            return $"{who} is not yet valid: valid from {certificate.NotBefore.ToUniversalTime():O}.";
        if (utc > certificate.NotAfter.ToUniversalTime())
            return $"{who} has expired: valid until {certificate.NotAfter.ToUniversalTime():O}.";

        if (revocation.Mode == X509RevocationMode.NoCheck || !NamesRevocationSources(certificate))
            return null;

        // The pin is the trust, so the issuer path is found first (local stores, AIA caIssuers) and then made the root
        // of the chain the revocation check runs on: Windows does not fetch a CRL for a chain to an untrusted root, and
        // the issuer is what signs the CRL. No issuer found leaves the status undetermined.
        X509Certificate2[] issuers;
        using (var discovery = new X509Chain())
        {
            discovery.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            discovery.ChainPolicy.VerificationTime = utc;
            discovery.ChainPolicy.VerificationFlags = X509VerificationFlags.AllowUnknownCertificateAuthority;
            discovery.Build(certificate);
            issuers = discovery.ChainElements.Skip(1).Select(e => e.Certificate).ToArray();
        }

        var status = X509ChainStatusFlags.RevocationStatusUnknown;
        if (issuers.Length > 0)
        {
            using var chain = new X509Chain();
            chain.ChainPolicy.RevocationMode = revocation.Mode;
            chain.ChainPolicy.RevocationFlag = X509RevocationFlag.EndCertificateOnly;
            chain.ChainPolicy.VerificationTime = utc;
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.Add(issuers[^1]);
            chain.ChainPolicy.ExtraStore.AddRange(issuers[..^1]);
            chain.Build(certificate);
            status = chain.ChainElements[0].ChainElementStatus.Aggregate(X509ChainStatusFlags.NoError, (all, s) => all | s.Status);
        }
        if ((status & X509ChainStatusFlags.Revoked) != 0)
            return $"{who} has been revoked.";
        if ((status & (X509ChainStatusFlags.RevocationStatusUnknown | X509ChainStatusFlags.OfflineRevocation)) != 0 && !revocation.SoftFail)
            return $"{who}: its revocation status could not be determined (CRL or OCSP responder unreachable).";
        return null;
    }

    /// <summary>Throws <see cref="CryptographicException"/> when a received message's signer may not be used now.</summary>
    public static void EnsureSigner(X509Certificate2 signer, DateTimeOffset now, RevocationPolicy revocation)
    {
        if (Problem(signer, now, "The signing certificate", revocation) is { } problem)
            throw new CryptographicException(problem);
    }

    private static bool NamesRevocationSources(X509Certificate2 certificate) =>
        certificate.Extensions.Any(e => e.Oid?.Value is CrlDistributionPointsOid or AuthorityInformationAccessOid);
}

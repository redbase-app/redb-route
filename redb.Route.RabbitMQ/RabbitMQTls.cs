using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;

namespace redb.Route.RabbitMQ;

/// <summary>How the connection authenticates to the broker.</summary>
public enum RabbitMQAuthMechanism
{
    /// <summary>Username and password (SASL PLAIN), the default.</summary>
    Plain,

    /// <summary>
    /// The TLS client certificate (SASL EXTERNAL): the broker takes the user from the certificate, and no password is
    /// sent. Needs TLS and a client certificate, and the broker's <c>rabbitmq_auth_mechanism_ssl</c> plugin.
    /// </summary>
    External,
}

/// <summary>The TLS settings of one connection, resolved from the endpoint URI or from a named factory.</summary>
/// <param name="Enabled">TLS on.</param>
/// <param name="ServerName">The name the broker certificate must carry; null checks each host against its own name.</param>
/// <param name="ClientCertificate">The certificate presented for mutual TLS, loaded and checked for validity.</param>
/// <param name="Protocols">Allowed TLS versions; <see cref="SslProtocols.None"/> lets the operating system choose.</param>
/// <param name="CaCertificates">Trust only these roots for the broker certificate instead of the system store.</param>
/// <param name="RevocationMode">Revocation check of the broker certificate chain.</param>
/// <param name="RevocationSoftFail">Accept a broker certificate whose revocation status cannot be determined.</param>
internal sealed record RabbitMQTlsSettings(
    bool Enabled,
    string? ServerName,
    X509Certificate2? ClientCertificate,
    SslProtocols Protocols,
    X509Certificate2Collection? CaCertificates,
    X509RevocationMode RevocationMode,
    bool RevocationSoftFail)
{
    /// <summary>No TLS.</summary>
    public static readonly RabbitMQTlsSettings Off =
        new(false, null, null, SslProtocols.None, null, X509RevocationMode.NoCheck, false);
}

/// <summary>Loading and checking the TLS material of a RabbitMQ connection, and the SSL options built from it.</summary>
internal static class RabbitMQTls
{
    /// <summary>The TLS versions a connection may be limited to; older ones are broken and refused.</summary>
    public const SslProtocols AllowedProtocols = SslProtocols.Tls12 | SslProtocols.Tls13;

    /// <summary>
    /// The TLS and authentication rules, the same for the endpoint URI and a named factory: a TLS setting without TLS
    /// would be ignored in silence, so it is refused, as are settings that contradict each other.
    /// </summary>
    public static void Validate(TlsRules r, string owner)
    {
        if (!r.Ssl)
        {
            var needTls = new List<string>();
            if (!string.IsNullOrEmpty(r.ServerName)) needTls.Add("sslServerName");
            if (!string.IsNullOrEmpty(r.CertPath)) needTls.Add("sslCertPath");
            if (!string.IsNullOrEmpty(r.CertPassphrase)) needTls.Add("sslCertPassword");
            if (r.ClientCertificateObject) needTls.Add("ClientCertificate");
            if (!string.IsNullOrEmpty(r.CaCertPath)) needTls.Add("sslCaCertPath");
            if (r.CaCertificatesObject) needTls.Add("SslCaCertificates");
            if (r.Protocols != SslProtocols.None) needTls.Add("sslProtocols");
            if (r.Revocation != X509RevocationMode.NoCheck) needTls.Add("revocationMode");
            if (r.SoftFail) needTls.Add("revocationSoftFail");
            if (needTls.Count > 0)
                throw new ArgumentException(
                    $"{owner}: {string.Join(", ", needTls)} only take effect over TLS, and ssl is off. Set ssl=true, or remove them.");
        }

#pragma warning disable SYSLIB0039 // named to refuse them
        if ((r.Protocols & ~AllowedProtocols) != SslProtocols.None)
            throw new ArgumentException(
                $"{owner}: sslProtocols '{r.Protocols}' is not allowed: only Tls12 and Tls13 (SSL 3.0, TLS 1.0 and TLS 1.1 are broken).");
#pragma warning restore SYSLIB0039

        if (!string.IsNullOrEmpty(r.CertPassphrase) && string.IsNullOrEmpty(r.CertPath))
            throw new ArgumentException($"{owner}: sslCertPassword is given without sslCertPath.");
        if (r.ClientCertificateObject && !string.IsNullOrEmpty(r.CertPath))
            throw new ArgumentException($"{owner}: give the client certificate once, as ClientCertificate or as SslCertPath, not both.");
        if (r.CaCertificatesObject && !string.IsNullOrEmpty(r.CaCertPath))
            throw new ArgumentException($"{owner}: give the CA certificates once, as SslCaCertificates or as SslCaCertPath, not both.");
        if (r.SoftFail && r.Revocation == X509RevocationMode.NoCheck)
            throw new ArgumentException($"{owner}: revocationSoftFail is set but revocationMode is NoCheck, so nothing is checked.");

        if (r.Auth == RabbitMQAuthMechanism.External
            && (!r.Ssl || (string.IsNullOrEmpty(r.CertPath) && !r.ClientCertificateObject)))
            throw new ArgumentException(
                $"{owner}: authMechanism=External logs in with the TLS client certificate, so it needs ssl=true and a client certificate.");
    }

    /// <summary>The settings <see cref="Validate"/> checks, from either source.</summary>
    internal sealed record TlsRules(
        bool Ssl, string? ServerName, string? CertPath, string? CertPassphrase, bool ClientCertificateObject,
        string? CaCertPath, bool CaCertificatesObject, SslProtocols Protocols, X509RevocationMode Revocation, bool SoftFail,
        RabbitMQAuthMechanism Auth);

    /// <summary>Loads the client certificate (PFX) and refuses one outside its validity period.</summary>
    public static X509Certificate2 LoadClientCertificate(string path, string? passphrase, string owner)
    {
        X509Certificate2 certificate;
        try
        {
#if NET9_0_OR_GREATER
            certificate = X509CertificateLoader.LoadPkcs12FromFile(path, passphrase);
#else
            certificate = new X509Certificate2(path, passphrase);
#endif
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"{owner}: the client certificate '{path}' could not be loaded: {ex.Message}", ex);
        }

        EnsureClientCertificateUsable(certificate, owner);
        return certificate;
    }

    /// <summary>
    /// Refuses a client certificate without a private key or outside its validity period: the broker would refuse the
    /// handshake with a bare "certificate unknown", so the connector says which certificate and why first.
    /// </summary>
    public static void EnsureClientCertificateUsable(X509Certificate2 certificate, string owner)
    {
        var who = $"{owner}: the client certificate {certificate.Subject} ({certificate.Thumbprint})";
        if (!certificate.HasPrivateKey)
            throw new CryptographicException($"{who} has no private key, so it cannot be presented.");
        var now = DateTime.UtcNow;
        if (now < certificate.NotBefore.ToUniversalTime())
            throw new CryptographicException($"{who} is not yet valid: valid from {certificate.NotBefore.ToUniversalTime():O}.");
        if (now > certificate.NotAfter.ToUniversalTime())
            throw new CryptographicException($"{who} has expired: valid until {certificate.NotAfter.ToUniversalTime():O}.");
    }

    /// <summary>Loads the trusted root certificates from a PEM file (one or more certificates).</summary>
    public static X509Certificate2Collection LoadCaCertificates(string path, string owner)
    {
        var collection = new X509Certificate2Collection();
        try
        {
            collection.ImportFromPemFile(path);
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"{owner}: the CA certificates '{path}' could not be loaded: {ex.Message}", ex);
        }

        if (collection.Count == 0)
            throw new InvalidOperationException($"{owner}: '{path}' holds no PEM certificate to trust.");
        return collection;
    }

    /// <summary>The SSL options of one host of the connection.</summary>
    public static SslOption OptionFor(string host, RabbitMQTlsSettings tls, ILogger? logger)
    {
        if (!tls.Enabled)
            return new SslOption();

        var option = new SslOption
        {
            Enabled = true,
            ServerName = string.IsNullOrEmpty(tls.ServerName) ? host : tls.ServerName,
            Version = tls.Protocols,
        };
        if (tls.ClientCertificate is { } client)
            option.Certs = new X509CertificateCollection { client };
        if (tls.CaCertificates is { Count: > 0 } || tls.RevocationMode != X509RevocationMode.NoCheck)
            option.CertificateValidationCallback = BrokerCertificateValidator(tls, logger);
        return option;
    }

    /// <summary>
    /// Checks the broker certificate against the configured roots and revocation policy. The host name is checked as
    /// always; the chain is built again under the connection's trust rather than the system's.
    /// </summary>
    private static RemoteCertificateValidationCallback BrokerCertificateValidator(RabbitMQTlsSettings tls, ILogger? logger)
        => (_, certificate, presented, errors) =>
        {
            if (certificate is null || (errors & SslPolicyErrors.RemoteCertificateNotAvailable) != 0)
            {
                logger?.LogError("RabbitMQ TLS: the broker presented no certificate.");
                return false;
            }

            if ((errors & SslPolicyErrors.RemoteCertificateNameMismatch) != 0)
            {
                logger?.LogError("RabbitMQ TLS: the broker certificate {Subject} does not name the server the connection expects.",
                    certificate.Subject);
                return false;
            }

#if NET9_0_OR_GREATER
            using var broker = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
#else
            using var broker = new X509Certificate2(certificate);
#endif
            using var chain = new X509Chain();
            chain.ChainPolicy.RevocationMode = tls.RevocationMode;
            chain.ChainPolicy.RevocationFlag = X509RevocationFlag.ExcludeRoot;
            if (tls.CaCertificates is { Count: > 0 } roots)
            {
                chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                chain.ChainPolicy.CustomTrustStore.AddRange(roots);
            }
            if (presented is not null)
                foreach (var element in presented.ChainElements.Skip(1))
                    chain.ChainPolicy.ExtraStore.Add(element.Certificate);

            if (chain.Build(broker))
                return true;

            var status = chain.ChainStatus.Aggregate(X509ChainStatusFlags.NoError, (all, s) => all | s.Status);
            const X509ChainStatusFlags undetermined = X509ChainStatusFlags.RevocationStatusUnknown | X509ChainStatusFlags.OfflineRevocation;
            if ((status & ~undetermined) == X509ChainStatusFlags.NoError)
            {
                // Only the revocation status is missing. A certificate that names no CRL distribution point and no
                // authority information access cannot be checked at all and passes, as in the AS4 connector (Domibus);
                // one that names a source it could not reach passes only with soft-fail.
                var unreachable = chain.ChainElements
                    .Where(e => e.ChainElementStatus.Any(s => (s.Status & undetermined) != 0))
                    .Where(e => NamesRevocationSources(e.Certificate))
                    .ToList();
                if (unreachable.Count == 0)
                    return true;
                if (tls.RevocationSoftFail)
                {
                    logger?.LogWarning("RabbitMQ TLS: the revocation status of the broker certificate {Subject} could not be " +
                        "determined; accepted because revocationSoftFail is set.", broker.Subject);
                    return true;
                }
            }

            logger?.LogError("RabbitMQ TLS: the broker certificate {Subject} ({Thumbprint}) was rejected: {Status}.",
                broker.Subject, broker.Thumbprint, status);
            return false;
        };

    private const string CrlDistributionPointsOid = "2.5.29.31";
    private const string AuthorityInformationAccessOid = "1.3.6.1.5.5.7.1.1";

    private static bool NamesRevocationSources(X509Certificate2 certificate) =>
        certificate.Extensions.Any(e => e.Oid?.Value is CrlDistributionPointsOid or AuthorityInformationAccessOid);
}

using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using redb.Route.Core;

namespace redb.Route.As4;

/// <summary>
/// Our AS4 node: our party identity, our keys and the trading partners we
/// deal with. Registered once by name and referenced from endpoint URIs with <c>connectionFactory=name</c>,
/// so certificates and passwords never travel in a URI (which is the route key and ends up in logs, spans
/// and the dashboard).
/// <example><code>
/// context.AddToRegistry("node", new As4ConnectionFactory
/// {
///     OurPartyId = "...",
///     SigningCertificate = ourPfx,
///     DecryptionCertificates = { ourPfx },
///     Partners = { new As4Partner { Name = "acme", PartyId = "...", Service = "...", Action = "...",
///                                   PartnerSigningCertificates = { acmeCer }, PartnerEncryptionCertificate = acmeCer } },
/// });
/// </code></example>
/// In Route-XML the same node is a <c>&lt;bean&gt;</c> whose <c>Partners</c> property is a <c>&lt;list&gt;</c> of
/// <c>&lt;ref bean="…"/&gt;</c> or nested <c>&lt;bean&gt;</c> partners.
/// </summary>
public sealed class As4ConnectionFactory
{
    /// <summary>Our <c>eb:PartyId</c>. Exactly one, as eDelivery requires.</summary>
    public string? OurPartyId { get; set; }

    /// <summary>Our <c>eb:PartyId/@type</c>; omitted when null.</summary>
    public string? OurPartyIdType { get; set; }

    /// <summary>
    /// Host part of the message ids we generate (<c>uuid@host</c>). Set it to a public name: the default is a
    /// fixed placeholder, never the machine name, so internal host names do not leak to partners.
    /// </summary>
    public string ExternalHostName { get; set; } = "redb.route";

    /// <summary>Our certificate with its private key: signs our messages and receipts.</summary>
    public X509Certificate2? SigningCertificate { get; set; }

    /// <summary>
    /// Our certificates with private keys that decrypt payloads partners encrypt for us. More than one while
    /// we rotate our key and partners still encrypt for either; the one a message names is used.
    /// </summary>
    public IList<X509Certificate2> DecryptionCertificates { get; set; } = new List<X509Certificate2>();

    /// <summary>
    /// TLS versions our sender offers. Default TLS 1.2 and 1.3: eDelivery AS4 1.16 requires 1.2, allows 1.3 and forbids
    /// SSL and TLS 1.0 / 1.1, so anything else is refused when the node is validated. The same default as Holodeck
    /// (<c>AllowedProtocols</c>), phase4 and Domibus; the name is the AMQP connection factory's. The receive side's
    /// versions are the shared host's.
    /// </summary>
    public SslProtocols SslProtocols { get; set; } = SslProtocols.Tls12 | SslProtocols.Tls13;

    /// <summary>
    /// Revocation check of the certificates the node pins: the partners' signing and encryption certificates, our own
    /// signing certificate before sending, and allow-listed TLS client certificates. <see cref="X509RevocationMode.NoCheck"/>
    /// (default, as Camel/CXF, Holodeck and phase4), <see cref="X509RevocationMode.Online"/> (CRL / OCSP the certificate
    /// points at; the OS caches them) or <see cref="X509RevocationMode.Offline"/> (the cache only). The name is ASP.NET's
    /// <c>CertificateAuthenticationOptions.RevocationMode</c>. A certificate that names no CRL distribution point and no
    /// authority information access passes, as in Domibus.
    /// </summary>
    public X509RevocationMode RevocationMode { get; set; } = X509RevocationMode.NoCheck;

    /// <summary>
    /// Accept a certificate whose revocation status cannot be determined (responder unreachable). Default false: refused,
    /// as phase4 and Domibus refuse it.
    /// </summary>
    public bool RevocationSoftFail { get; set; }

    /// <summary>The revocation policy of this node.</summary>
    internal Security.RevocationPolicy Revocation => new(RevocationMode, RevocationSoftFail);

    /// <summary>Our TLS client certificate for sending to partners that require mutual TLS; null for none.</summary>
    public X509Certificate2? ClientCertificate { get; set; }

    /// <summary>PFX the receive server presents over TLS, when the endpoint URI does not give one.</summary>
    public string? SslCertPath { get; set; }

    /// <summary>Password for <see cref="SslCertPath"/>.</summary>
    [Sensitive]
    public string? SslCertPassword { get; set; }

    /// <summary>
    /// Whether the receive server asks partners for a TLS client certificate, when the endpoint URI does not say.
    /// Needs a TLS (<c>as4s</c>) receive endpoint.
    /// </summary>
    public As4ClientCertificateMode ClientCertificateMode { get; set; } = As4ClientCertificateMode.NoCertificate;

    /// <summary>Thumbprints (comma-separated) an accepted client certificate must match, when the endpoint URI gives none.</summary>
    public string? AllowedClientThumbprints { get; set; }

    /// <summary>The agreements of this node, one per trading partner, told apart by <see cref="As4Partner.Name"/>.</summary>
    public IList<As4Partner> Partners { get; set; } = new List<As4Partner>();

    /// <summary>Throws when the node cannot be used; called when an endpoint that names it starts.</summary>
    /// <param name="name">The factory's registry name, for the error text.</param>
    public void Validate(string name)
    {
        var owner = $"AS4 connection factory '{name}'";

        if (string.IsNullOrWhiteSpace(OurPartyId))
            throw new InvalidOperationException($"{owner}: OurPartyId is required.");
        if (string.IsNullOrWhiteSpace(ExternalHostName))
            throw new InvalidOperationException($"{owner}: ExternalHostName must not be empty.");
        if (SslProtocols == SslProtocols.None || (SslProtocols & ~(SslProtocols.Tls12 | SslProtocols.Tls13)) != 0)
            throw new InvalidOperationException($"{owner}: SslProtocols '{SslProtocols}' is not allowed; eDelivery AS4 1.16 permits TLS 1.2 and TLS 1.3 only.");
        if (SigningCertificate is not { HasPrivateKey: true })
            throw new InvalidOperationException($"{owner}: SigningCertificate with a private key is required — every AS4 message and receipt is signed.");
        if (DecryptionCertificates is null || DecryptionCertificates.Count == 0)
            throw new InvalidOperationException($"{owner}: DecryptionCertificates is empty — every received payload is encrypted.");
        if (DecryptionCertificates.Any(c => c is not { HasPrivateKey: true }))
            throw new InvalidOperationException($"{owner}: every DecryptionCertificates entry needs a private key.");
        if (Partners is null || Partners.Count == 0)
            throw new InvalidOperationException($"{owner}: Partners is empty.");
        if (Partners.Any(p => p is null))
            throw new InvalidOperationException($"{owner}: Partners contains an empty entry.");
        if (Partners.FirstOrDefault(p => string.IsNullOrWhiteSpace(p.Name)) is { } unnamed)
            throw new InvalidOperationException($"{owner}: a partner has no Name (PartyId '{unnamed.PartyId}').");

        var duplicates = Partners.GroupBy(p => p.Name!, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        if (duplicates.Count > 0)
            throw new InvalidOperationException($"{owner}: partner names repeat: {string.Join(", ", duplicates)}.");
    }
}

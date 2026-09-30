using System.Security.Cryptography.X509Certificates;

namespace redb.Route.As4;

/// <summary>
/// The agreement with one trading partner: who they are, what we exchange, how it is secured. This is the
/// partner half of an ebMS P-Mode; the node half (our identity and keys) is <see cref="As4ConnectionFactory"/>.
/// <para>
/// A partner lives in <see cref="As4ConnectionFactory.Partners"/> and is named by <see cref="Name"/>: the
/// name a send endpoint gives in <c>partner=</c> and the value of the <c>redbAs4.partner</c> header on
/// receipt. In Route-XML it is a nested <c>&lt;bean&gt;</c> or a <c>&lt;ref&gt;</c> in the node's <c>Partners</c> list.
/// </para>
/// <para>
/// Defaults follow the eDelivery AS4 1.16 common profile: every message is signed, its payloads encrypted
/// and compressed, and the receiver answers with a signed non-repudiation receipt in the HTTP response.
/// Those are not options: the profile mandates them.
/// </para>
/// </summary>
public sealed class As4Partner
{
    /// <summary>Default <c>eb:Role</c> of the side that sends the first message of an exchange.</summary>
    public const string InitiatorRole = "http://docs.oasis-open.org/ebxml-msg/ebms/v3.0/ns/core/200704/initiator";

    /// <summary>Default <c>eb:Role</c> of the side that receives it.</summary>
    public const string ResponderRole = "http://docs.oasis-open.org/ebxml-msg/ebms/v3.0/ns/core/200704/responder";

    // ── Identity ─────────────────────────────────────────────────────────────

    /// <summary>Name of the agreement within its node: what <c>partner=</c> refers to. Required, unique per node.</summary>
    public string? Name { get; set; }

    /// <summary>The partner's <c>eb:PartyId</c>. Exactly one per side, as eDelivery requires.</summary>
    public string? PartyId { get; set; }

    /// <summary>The partner's <c>eb:PartyId/@type</c>; omitted when null.</summary>
    public string? PartyIdType { get; set; }

    /// <summary>Our <c>eb:Role</c> in exchanges with this partner. Default: initiator.</summary>
    public string OurRole { get; set; } = InitiatorRole;

    /// <summary>The partner's <c>eb:Role</c>. Default: responder.</summary>
    public string PartnerRole { get; set; } = ResponderRole;

    // ── Agreement ────────────────────────────────────────────────────────────

    /// <summary><c>eb:AgreementRef</c>; omitted when null. When present on both sides it selects the agreement on receipt.</summary>
    public string? AgreementRef { get; set; }

    /// <summary><c>eb:AgreementRef/@type</c>; omitted when null.</summary>
    public string? AgreementRefType { get; set; }

    /// <summary><c>eb:AgreementRef/@pmode</c>; omitted when null.</summary>
    public string? PModeId { get; set; }

    /// <summary><c>eb:Service</c> of the request leg.</summary>
    public string? Service { get; set; }

    /// <summary><c>eb:Service/@type</c> of the request leg; omitted when null.</summary>
    public string? ServiceType { get; set; }

    /// <summary><c>eb:Action</c> of the request leg.</summary>
    public string? Action { get; set; }

    /// <summary>Whether a route may send a different <c>eb:Service</c> than <see cref="Service"/> (option or header).</summary>
    public bool AllowOverrideService { get; set; }

    /// <summary>Whether a route may send a different <c>eb:Action</c> than <see cref="Action"/> (option or header).</summary>
    public bool AllowOverrideAction { get; set; }

    /// <summary>
    /// The reply leg of a Two-Way / Push-and-Push exchange: the user message the responder pushes back with
    /// <c>eb:RefToMessageId</c> set to the request. Null for one-way agreements.
    /// </summary>
    public As4Leg? ReplyLeg { get; set; }

    // ── Security ─────────────────────────────────────────────────────────────

    /// <summary>
    /// The partner's certificates that verify its signatures. More than one while the partner rotates its
    /// key; a signature by any of them is the partner's.
    /// </summary>
    public IList<X509Certificate2> PartnerSigningCertificates { get; set; } = new List<X509Certificate2>();

    /// <summary>The partner's certificate we encrypt payloads for.</summary>
    public X509Certificate2? PartnerEncryptionCertificate { get; set; }

    /// <summary>How our signatures reference our certificate. Default: <see cref="As4KeyReference.BinarySecurityToken"/>.</summary>
    public As4KeyReference KeyReference { get; set; } = As4KeyReference.BinarySecurityToken;

    /// <summary>Signature algorithm URI. Default RSA-SHA256.</summary>
    public string SignatureAlgorithm { get; set; } = As4Algorithms.RsaSha256;

    /// <summary>Digest algorithm URI for signature references. Default SHA-256.</summary>
    public string DigestAlgorithm { get; set; } = As4Algorithms.Sha256;

    /// <summary>Payload encryption algorithm URI. Default AES-128-GCM.</summary>
    public string DataEncryptionAlgorithm { get; set; } = As4Algorithms.Aes128Gcm;

    /// <summary>Key transport algorithm URI. Default RSA-OAEP (XML Encryption 1.1).</summary>
    public string KeyTransportAlgorithm { get; set; } = As4Algorithms.RsaOaep;

    /// <summary>Mask generation function URI for key transport. Default MGF1-SHA256.</summary>
    public string MaskGenerationAlgorithm { get; set; } = As4Algorithms.Mgf1Sha256;

    /// <summary>Digest URI for OAEP key transport. Default SHA-256.</summary>
    public string KeyTransportDigestAlgorithm { get; set; } = As4Algorithms.Sha256;

    /// <summary>
    /// Lifetime of a <c>wsu:Timestamp</c> in the messages and receipts we sign; null (default) signs none. The eDelivery
    /// AS4 1.16 policy of Domibus, the reference implementation (<c>eDeliveryAS4Policy.xml</c>), has no
    /// <c>IncludeTimestamp</c> and a <c>Strict</c> layout, so a timestamp fails it; set a lifetime for a partner whose
    /// policy asks for one. A received timestamp is always checked, whatever this setting.
    /// </summary>
    public TimeSpan? TimestampTtl { get; set; }

    /// <summary>
    /// Clock skew accepted when checking the partner's <c>wsu:Timestamp</c>: how far in the future its <c>Created</c>
    /// may lie and how long past its <c>Expires</c> (WSS4J futureTimeToLive). Default 5 minutes.
    /// </summary>
    public TimeSpan TimestampTolerance { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Default <see cref="TimestampTimeToLive"/>: 5 minutes, WSS4J's timeStampTTL (300 s).</summary>
    public static readonly TimeSpan DefaultTimestampTimeToLive = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How old the <c>Created</c> of a received <c>wsu:Timestamp</c> may be (WSS4J timeStampTTL, CXF
    /// <c>ws-security.timestamp.timeToLive</c>). It bounds a replayed message whatever its <c>Expires</c> says.
    /// Default <see cref="DefaultTimestampTimeToLive"/>.
    /// </summary>
    public TimeSpan TimestampTimeToLive { get; set; } = DefaultTimestampTimeToLive;

    /// <summary>
    /// Gzip the payloads we send (AS4 compression; eDelivery AS4 1.16 requires support for it). Default true.
    /// Received payloads are decompressed whenever their part properties say so, whatever this setting.
    /// </summary>
    public bool CompressPayloads { get; set; } = true;

    /// <summary>Throws when the agreement cannot be used; called when an endpoint of its node starts.</summary>
    public void Validate()
    {
        var owner = $"AS4 partner '{Name}'";

        if (string.IsNullOrWhiteSpace(PartyId))
            throw new InvalidOperationException($"{owner}: PartyId is required.");
        if (string.IsNullOrWhiteSpace(OurRole) || string.IsNullOrWhiteSpace(PartnerRole))
            throw new InvalidOperationException($"{owner}: OurRole and PartnerRole are required.");

        new As4Leg { Service = Service, ServiceType = ServiceType, Action = Action }.Validate($"{owner} request leg");
        ReplyLeg?.Validate($"{owner} reply leg");

        if (PartnerSigningCertificates is null || PartnerSigningCertificates.Count == 0 || PartnerSigningCertificates.Any(c => c is null))
            throw new InvalidOperationException($"{owner}: PartnerSigningCertificates is required — every AS4 message and receipt is signed.");
        if (PartnerEncryptionCertificate is null)
            throw new InvalidOperationException($"{owner}: PartnerEncryptionCertificate is required — every payload is encrypted.");

        if (!As4Algorithms.IsSupportedSignature(SignatureAlgorithm))
            throw new InvalidOperationException($"{owner}: unsupported signature algorithm '{SignatureAlgorithm}'.");
        if (!As4Algorithms.IsSupportedDigest(DigestAlgorithm))
            throw new InvalidOperationException($"{owner}: unsupported digest algorithm '{DigestAlgorithm}'.");
        if (!As4Algorithms.IsSupportedDataEncryption(DataEncryptionAlgorithm))
            throw new InvalidOperationException($"{owner}: unsupported data encryption algorithm '{DataEncryptionAlgorithm}'.");
        if (!As4Algorithms.IsSupportedKeyTransport(KeyTransportAlgorithm))
            throw new InvalidOperationException($"{owner}: unsupported key transport algorithm '{KeyTransportAlgorithm}'.");
        if (!As4Algorithms.IsSupportedMaskGeneration(MaskGenerationAlgorithm))
            throw new InvalidOperationException($"{owner}: unsupported mask generation function '{MaskGenerationAlgorithm}'.");
        if (!As4Algorithms.IsSupportedDigest(KeyTransportDigestAlgorithm))
            throw new InvalidOperationException($"{owner}: unsupported key transport digest '{KeyTransportDigestAlgorithm}'.");

        if (TimestampTtl is { } ttl && ttl <= TimeSpan.Zero)
            throw new InvalidOperationException($"{owner}: TimestampTtl must be positive when set.");
        if (TimestampTolerance < TimeSpan.Zero)
            throw new InvalidOperationException($"{owner}: TimestampTolerance must not be negative.");
        if (TimestampTimeToLive <= TimeSpan.Zero)
            throw new InvalidOperationException($"{owner}: TimestampTimeToLive must be positive.");
    }
}

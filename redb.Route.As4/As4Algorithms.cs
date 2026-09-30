namespace redb.Route.As4;

/// <summary>
/// Algorithm identifiers used by AS4 message security, and the set this connector supports. The supported
/// set is exactly what the eDelivery AS4 1.16 common profile mandates; an agreement naming anything else is
/// refused when it is validated, not when the first message arrives.
/// </summary>
public static class As4Algorithms
{
    /// <summary>RSA with SHA-256 signature (XML Signature).</summary>
    public const string RsaSha256 = "http://www.w3.org/2001/04/xmldsig-more#rsa-sha256";

    /// <summary>SHA-256 digest (XML Encryption namespace URI, as XML Signature uses it).</summary>
    public const string Sha256 = "http://www.w3.org/2001/04/xmlenc#sha256";

    /// <summary>AES-128 in GCM mode (XML Encryption 1.1).</summary>
    public const string Aes128Gcm = "http://www.w3.org/2009/xmlenc11#aes128-gcm";

    /// <summary>RSA-OAEP key transport (XML Encryption 1.1), with explicit MGF and digest.</summary>
    public const string RsaOaep = "http://www.w3.org/2009/xmlenc11#rsa-oaep";

    /// <summary>MGF1 with SHA-256 mask generation (XML Encryption 1.1).</summary>
    public const string Mgf1Sha256 = "http://www.w3.org/2009/xmlenc11#mgf1sha256";

    private static readonly HashSet<string> Signatures = new(StringComparer.Ordinal) { RsaSha256 };
    private static readonly HashSet<string> Digests = new(StringComparer.Ordinal) { Sha256 };
    private static readonly HashSet<string> DataEncryption = new(StringComparer.Ordinal) { Aes128Gcm };
    private static readonly HashSet<string> KeyTransports = new(StringComparer.Ordinal) { RsaOaep };
    private static readonly HashSet<string> MaskGenerations = new(StringComparer.Ordinal) { Mgf1Sha256 };

    /// <summary>Whether <paramref name="uri"/> is a supported signature algorithm.</summary>
    public static bool IsSupportedSignature(string? uri) => uri is not null && Signatures.Contains(uri);

    /// <summary>Whether <paramref name="uri"/> is a supported digest algorithm.</summary>
    public static bool IsSupportedDigest(string? uri) => uri is not null && Digests.Contains(uri);

    /// <summary>Whether <paramref name="uri"/> is a supported payload encryption algorithm.</summary>
    public static bool IsSupportedDataEncryption(string? uri) => uri is not null && DataEncryption.Contains(uri);

    /// <summary>Whether <paramref name="uri"/> is a supported key transport algorithm.</summary>
    public static bool IsSupportedKeyTransport(string? uri) => uri is not null && KeyTransports.Contains(uri);

    /// <summary>Whether <paramref name="uri"/> is a supported mask generation function.</summary>
    public static bool IsSupportedMaskGeneration(string? uri) => uri is not null && MaskGenerations.Contains(uri);
}

using System.Security.Cryptography.X509Certificates;
using redb.Route.Abstractions;
using redb.Route.As2.Crypto;
using redb.Route.Extensions;

namespace redb.Route.As2;

/// <summary>
/// The effective AS2 trading configuration of an endpoint: certificates, identifiers and the agreed profile. It comes
/// from ONE place: the named <see cref="As2ConnectionFactory"/> when the endpoint names one (its URI may then not set
/// agreement options, see <see cref="AgreementOptions"/>), else the endpoint's inline options. Consumer, MDN receiver
/// and producer all resolve through here, once, when they start.
/// </summary>
internal sealed class As2Profile
{
    public X509Certificate2? OurCertificate { get; init; }
    public X509Certificate2? PartnerCertificate { get; init; }
    public string As2From { get; init; } = "";
    public string As2To { get; init; } = "";
    public string PartnerUrl { get; init; } = "";
    public bool Sign { get; init; }
    public bool Encrypt { get; init; }
    public bool Compress { get; init; }
    public string SignAlg { get; init; } = "sha-256";
    public string EncryptAlg { get; init; } = "aes-128-cbc";
    public As2MdnMode MdnMode { get; init; }
    public bool SignedMdn { get; init; }
    public bool RequireValidMdn { get; init; }
    public string? AsyncMdnUrl { get; init; }
    public IReadOnlyList<string> AsyncMdnAllowedHosts { get; init; } = [];
    public bool AllowLegacyAlgorithms { get; init; }

    /// <summary>
    /// The URI options that describe the agreement. With a connection factory named, the factory is the agreement and
    /// these are refused on the URI: a value there would otherwise be silently ignored, or silently win.
    /// </summary>
    public static readonly IReadOnlyList<string> AgreementOptions =
    [
        nameof(As2EndpointOptions.As2From), nameof(As2EndpointOptions.As2To),
        nameof(As2EndpointOptions.Sign), nameof(As2EndpointOptions.Encrypt), nameof(As2EndpointOptions.Compress),
        nameof(As2EndpointOptions.SignAlg), nameof(As2EndpointOptions.EncryptAlg),
        nameof(As2EndpointOptions.MdnMode), nameof(As2EndpointOptions.SignedMdn), nameof(As2EndpointOptions.RequireValidMdn),
        nameof(As2EndpointOptions.AsyncMdnUrl), nameof(As2EndpointOptions.AsyncMdnAllowedHosts),
        nameof(As2EndpointOptions.AllowLegacyAlgorithms),
    ];

    /// <summary>
    /// The effective, validated profile: the named connection factory, or the inline options. A set-but-unknown name
    /// fails loud, never a silent fallback to inline options; an invalid agreement throws naming what is wrong.
    /// </summary>
    public static As2Profile Resolve(IRouteContext? context, As2EndpointOptions options, string endpoint)
    {
        if (!string.IsNullOrEmpty(options.ConnectionFactory))
        {
            var factory = context.GetRequiredFromRegistry<As2ConnectionFactory>(options.ConnectionFactory);
            var profile = From(factory, options.PartnerUrl);
            profile.Validate($"AS2 connection factory '{options.ConnectionFactory}'");
            return profile;
        }

        var inline = new As2Profile
        {
            As2From = options.As2From ?? "",
            As2To = options.As2To ?? "",
            PartnerUrl = options.PartnerUrl ?? "",
            Sign = options.Sign,
            Encrypt = options.Encrypt,
            Compress = options.Compress,
            SignAlg = options.SignAlg,
            EncryptAlg = options.EncryptAlg,
            MdnMode = options.MdnMode,
            SignedMdn = options.SignedMdn,
            RequireValidMdn = options.RequireValidMdn,
            AsyncMdnUrl = string.IsNullOrEmpty(options.AsyncMdnUrl) ? null : options.AsyncMdnUrl,
            AsyncMdnAllowedHosts = (options.AsyncMdnAllowedHosts ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            AllowLegacyAlgorithms = options.AllowLegacyAlgorithms,
        };
        inline.Validate($"AS2 endpoint {endpoint}");
        return inline;
    }

    /// <summary>The profile of <paramref name="factory"/>; the partner URL is the factory's, else <paramref name="uriPartnerUrl"/>.</summary>
    public static As2Profile From(As2ConnectionFactory factory, string? uriPartnerUrl = null) => new()
    {
        OurCertificate = factory.OurCertificate,
        PartnerCertificate = factory.PartnerCertificate,
        As2From = factory.As2From ?? "",
        As2To = factory.As2To ?? "",
        PartnerUrl = !string.IsNullOrEmpty(factory.PartnerUrl) ? factory.PartnerUrl : uriPartnerUrl ?? "",
        Sign = factory.Sign,
        Encrypt = factory.Encrypt,
        Compress = factory.Compress,
        SignAlg = factory.SignAlg,
        EncryptAlg = factory.EncryptAlg,
        MdnMode = factory.MdnMode,
        SignedMdn = factory.SignedMdn,
        RequireValidMdn = factory.RequireValidMdn,
        AsyncMdnUrl = string.IsNullOrEmpty(factory.AsyncMdnUrl) ? null : factory.AsyncMdnUrl,
        AsyncMdnAllowedHosts = [.. factory.AsyncMdnAllowedHosts ?? []],
        AllowLegacyAlgorithms = factory.AllowLegacyAlgorithms,
    };

    /// <summary>Whether <paramref name="alg"/> is <c>sha-1</c> or <c>3des</c> under any of its spellings.</summary>
    public static bool IsLegacy(string alg) => alg.Trim().ToLowerInvariant() is "sha-1" or "sha1" or "3des" or "des-ede3-cbc";

    /// <summary>Checks what every role needs; throws <see cref="InvalidOperationException"/> naming <paramref name="owner"/>.</summary>
    public void Validate(string owner)
    {
        if (string.IsNullOrWhiteSpace(As2From))
            throw new InvalidOperationException($"{owner}: As2From (our AS2 identifier) is required.");
        if (string.IsNullOrWhiteSpace(As2To))
            throw new InvalidOperationException($"{owner}: As2To (the partner's AS2 identifier) is required.");

        // The digest is the MIC algorithm even when nothing is signed.
        if (!As2CryptoEngine.IsSupportedDigest(SignAlg))
            throw new InvalidOperationException($"{owner}: SignAlg '{SignAlg}' is not supported. Supported: sha-256, sha-384, sha-512 (sha-1 as legacy).");
        if (Encrypt && !As2CryptoEngine.IsSupportedEncryption(EncryptAlg))
            throw new InvalidOperationException($"{owner}: EncryptAlg '{EncryptAlg}' is not supported. Supported: aes-128-cbc, aes-192-cbc, aes-256-cbc (3des as legacy).");
        foreach (var (name, alg) in new[] { ("SignAlg", SignAlg), ("EncryptAlg", Encrypt ? EncryptAlg : "") })
            if (IsLegacy(alg) && !AllowLegacyAlgorithms)
                throw new InvalidOperationException(
                    $"{owner}: {name} '{alg}' is a legacy algorithm; set AllowLegacyAlgorithms only if the partner requires it.");

        // Our key signs messages and MDNs and decrypts what the partner encrypts; the partner's certificate verifies
        // its signatures and encrypts for it.
        var signsMdn = MdnMode != As2MdnMode.None && SignedMdn;
        if (Sign || Encrypt || signsMdn)
        {
            if (OurCertificate is null)
                throw new InvalidOperationException($"{owner}: OurCertificate is required (signing, decryption or signed MDNs are agreed).");
            if (!OurCertificate.HasPrivateKey)
                throw new InvalidOperationException($"{owner}: OurCertificate has no private key; it signs and decrypts.");
            if (PartnerCertificate is null)
                throw new InvalidOperationException($"{owner}: PartnerCertificate is required (signatures, encryption or signed MDNs are agreed).");
        }

        if (AsyncMdnUrl is not null && !IsHttpUrl(AsyncMdnUrl))
            throw new InvalidOperationException($"{owner}: AsyncMdnUrl '{AsyncMdnUrl}' is not an absolute http(s) URL.");
        if (!string.IsNullOrEmpty(PartnerUrl) && !IsHttpUrl(PartnerUrl))
            throw new InvalidOperationException($"{owner}: PartnerUrl '{redb.Route.Abstractions.EndpointUri.Sanitize(PartnerUrl)}' is not an absolute http(s) URL.");
    }

    private static bool IsHttpUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
}

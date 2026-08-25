using System.Security.Cryptography.X509Certificates;
using redb.Route.Abstractions;

namespace redb.Route.As2;

/// <summary>
/// The effective AS2 trading configuration for one exchange: certificates, identifiers and the agreed
/// profile. Resolved from a named <see cref="As2ConnectionFactory"/> in the registry when present, falling
/// back to the endpoint's inline options. Consumer and producer both resolve through here so partner
/// settings have a single source of truth.
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

    /// <summary>Resolves the effective profile: connection factory (if named) wins over inline options.</summary>
    public static As2Profile Resolve(IRouteContext? context, As2EndpointOptions options)
    {
        As2ConnectionFactory? f = !string.IsNullOrEmpty(options.ConnectionFactory)
            ? context?.GetFromRegistry<As2ConnectionFactory>(options.ConnectionFactory)
            : null;

        return new As2Profile
        {
            OurCertificate = f?.OurCertificate,
            PartnerCertificate = f?.PartnerCertificate,
            As2From = NonEmpty(f?.As2From, options.As2From) ?? "",
            As2To = NonEmpty(f?.As2To, options.As2To) ?? "",
            PartnerUrl = NonEmpty(f?.PartnerUrl, options.PartnerUrl) ?? "",
            Sign = f?.Sign ?? options.Sign,
            Encrypt = f?.Encrypt ?? options.Encrypt,
            Compress = f?.Compress ?? options.Compress,
            SignAlg = NonEmpty(f?.SignAlg, options.SignAlg) ?? "sha-256",
            EncryptAlg = NonEmpty(f?.EncryptAlg, options.EncryptAlg) ?? "aes-128-cbc",
            MdnMode = f?.MdnMode ?? options.MdnMode,
            SignedMdn = f?.SignedMdn ?? options.SignedMdn,
            RequireValidMdn = f?.RequireValidMdn ?? options.RequireValidMdn,
            AsyncMdnUrl = NonEmpty(f?.AsyncMdnUrl, options.AsyncMdnUrl),
        };
    }

    private static string? NonEmpty(string? primary, string? fallback)
        => !string.IsNullOrEmpty(primary) ? primary : (!string.IsNullOrEmpty(fallback) ? fallback : null);
}

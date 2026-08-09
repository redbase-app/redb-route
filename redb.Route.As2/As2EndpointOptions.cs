using redb.Route.Core;

namespace redb.Route.As2;

/// <summary>
/// Strongly-typed options for an AS2 endpoint, bound from URI query parameters by
/// <see cref="EndpointOptions.BindFromUri"/>. Secrets are marked <c>[Sensitive]</c> so they are
/// redacted in logs. A partner profile (certificates, IDs, algorithms) usually comes from a named
/// <see cref="As2ConnectionFactory"/> in the registry via <c>connectionFactory=name</c>; these
/// inline options are the container-free fallback. See <c>docs/as2/02-DESIGN.md §3</c>.
/// </summary>
public sealed class As2EndpointOptions : EndpointOptions
{
    // ── Transport ────────────────────────────────────────────────────────────
    /// <summary>Bind address for the AS2 receive server (consumer). Default 0.0.0.0.</summary>
    public string Host { get; set; } = "0.0.0.0";

    /// <summary>Listen port for the AS2 receive server (consumer). Default 4080.</summary>
    public int Port { get; set; } = 4080;

    /// <summary>Partner endpoint URL the producer POSTs to. Reconstructed from the URI when absent.</summary>
    public string? PartnerUrl { get; set; }

    /// <summary>Use HTTPS/TLS. Set automatically when the URI scheme is <c>as2s</c>.</summary>
    public bool UseTls { get; set; }

    /// <summary>Per-request timeout in milliseconds (producer). Default 30000.</summary>
    public int Timeout { get; set; } = 30000;

    // ── Partner binding ──────────────────────────────────────────────────────
    /// <summary>Name of an <see cref="As2ConnectionFactory"/> registered in the route context.</summary>
    public string? ConnectionFactory { get; set; }

    /// <summary>What a receive endpoint accepts: business messages (default) or async MDN receipts.</summary>
    public As2ReceiveMode Mode { get; set; } = As2ReceiveMode.Message;

    // ── Profile (inline; a connection factory takes precedence) ──────────────
    /// <summary>Sign outgoing / expect signed incoming (S/MIME). Default true.</summary>
    public bool Sign { get; set; } = true;

    /// <summary>Encrypt outgoing / expect encrypted incoming (S/MIME). Default true.</summary>
    public bool Encrypt { get; set; } = true;

    /// <summary>Compress the payload (RFC 3274). Default false.</summary>
    public bool Compress { get; set; }

    /// <summary>Signature digest algorithm (<c>sha-256</c>, <c>sha-384</c>, ...). Default sha-256.</summary>
    public string SignAlg { get; set; } = "sha-256";

    /// <summary>Encryption algorithm (<c>aes-128-cbc</c>, <c>aes-256-cbc</c>, ...). Default aes-128-cbc.</summary>
    public string EncryptAlg { get; set; } = "aes-128-cbc";

    /// <summary>MDN mode: <see cref="As2MdnMode.Sync"/>, <see cref="As2MdnMode.Async"/> or <see cref="As2MdnMode.None"/>.</summary>
    public As2MdnMode MdnMode { get; set; } = As2MdnMode.Sync;

    /// <summary>Request/produce a signed MDN. Default true.</summary>
    public bool SignedMdn { get; set; } = true;

    /// <summary>URL the partner posts asynchronous MDNs to (our receiver). Required for async MDN.</summary>
    public string? AsyncMdnUrl { get; set; }

    // ── Identity ─────────────────────────────────────────────────────────────
    /// <summary>Our AS2 identifier (<c>AS2-From</c> on send, expected <c>AS2-To</c> on receive).</summary>
    public string? As2From { get; set; }

    /// <summary>Partner AS2 identifier (<c>AS2-To</c> on send, expected <c>AS2-From</c> on receive).</summary>
    public string? As2To { get; set; }

    // ── Secrets ──────────────────────────────────────────────────────────────
    /// <summary>Password for the certificate/private-key material (redacted in logs).</summary>
    [Sensitive] public string? CertPassword { get; set; }

    /// <inheritdoc />
    public override void Validate()
    {
        if (Port is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(Port), Port, "AS2 port must be between 1 and 65535.");
        if (Timeout < 0)
            throw new ArgumentOutOfRangeException(nameof(Timeout), Timeout, "AS2 timeout must be non-negative.");
        if (MdnMode == As2MdnMode.Async && string.IsNullOrEmpty(AsyncMdnUrl) && string.IsNullOrEmpty(ConnectionFactory))
            throw new ArgumentException("Async MDN requires 'asyncMdnUrl' or a 'connectionFactory' that provides it.");
        if (Sign && !Crypto.As2CryptoEngine.IsSupportedDigest(SignAlg))
            throw new ArgumentException($"Unsupported AS2 signature algorithm '{SignAlg}'. Supported: sha-1, sha-256, sha-384, sha-512.");
        if (Encrypt && !Crypto.As2CryptoEngine.IsSupportedEncryption(EncryptAlg))
            throw new ArgumentException($"Unsupported AS2 encryption algorithm '{EncryptAlg}'. Supported: aes-128-cbc, aes-192-cbc, aes-256-cbc, 3des.");
    }
}

using redb.Route.Http;
using redb.Route.Core;

namespace redb.Route.As2;

/// <summary>
/// Strongly-typed options for an AS2 endpoint, bound from URI query parameters by
/// <see cref="EndpointOptions.BindFromUri"/>. Secrets are marked <c>[Sensitive]</c> so they are
/// redacted in logs. A partner profile (certificates, IDs, algorithms) usually comes from a named
/// <see cref="As2ConnectionFactory"/> in the registry via <c>connectionFactory=name</c>. The agreement has one
/// source: with a factory named, the inline agreement options below are refused on the URI; without one, they are the
/// whole agreement (no certificates, so only for an agreement that neither signs nor encrypts). See
/// <c>docs/as2/02-DESIGN.md §3</c>.
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

    // ── Admission limit (HTTP_CONCURRENCY_LIMITS_PLAN) ──

    /// <summary>
    /// Maximum concurrent executions of this receiver's pipeline. 0 (default) = unlimited.
    /// Overflow beyond the limit and <see cref="RequestQueueLimit"/> is shed with
    /// <see cref="RejectStatusCode"/> before any MIME/crypto work; AS2 partners retry on their
    /// own MDN timeouts, which makes early shedding cheaper than a saturated receiver.
    /// </summary>
    public int MaxConcurrentRequests { get; set; }

    /// <summary>Requests over the limit that WAIT for a permit (FIFO). 0 (default) = reject immediately.</summary>
    public int RequestQueueLimit { get; set; }

    /// <summary>Status code for a shed request. Default 429 Too Many Requests.</summary>
    public int RejectStatusCode { get; set; } = 429;

    /// <summary>Value of the <c>Retry-After</c> header on a shed request; 0 = do not send it. Default 1.</summary>
    public int RetryAfterSeconds { get; set; } = 1;

    /// <summary>
    /// PFX certificate the receive server presents to the trading partner. This is the TLS server
    /// certificate, a different thing from <see cref="As2ConnectionFactory.OurCertificate"/>, which
    /// signs and decrypts the S/MIME payload. May be left unset when the host carries a default
    /// (<c>AddRedbRouteHttpHosting(o =&gt; o.Tls.DefaultCertificatePath = ...)</c>); a receive
    /// endpoint with TLS and no certificate in either place refuses to bind.
    /// </summary>
    public string? SslCertPath { get; set; }

    /// <summary>Password for <see cref="SslCertPath"/>.</summary>
    [Sensitive]
    public string? SslCertPassword { get; set; }

    /// <summary>
    /// Timeout in milliseconds of an outgoing POST: the message on a send, the asynchronous MDN on a receive.
    /// Default 30000.
    /// </summary>
    public int Timeout { get; set; } = 30000;

    /// <summary>Default of <see cref="MaxRequestBodySize"/>: 100 MB, as the AS4 receiver.</summary>
    public const long DefaultMaxRequestBodySize = 100L * 1024 * 1024;

    /// <summary>Default of <see cref="MaxResponseBodySize"/>: 4 MB, ample for an MDN.</summary>
    public const long DefaultMaxResponseBodySize = 4L * 1024 * 1024;

    /// <summary>
    /// Receive side (messages and MDNs): the largest request body accepted, in bytes; a larger one is answered 413
    /// before it is read. The received message is processed in memory (body, decrypted content and payload), so this
    /// also bounds the memory one request takes. Default <see cref="DefaultMaxRequestBodySize"/>.
    /// </summary>
    public long MaxRequestBodySize { get; set; } = DefaultMaxRequestBodySize;

    /// <summary>
    /// Send side: the largest response body read, in bytes (the synchronous MDN). A larger one (a proxy's error page,
    /// say) fails the send without being read. Default <see cref="DefaultMaxResponseBodySize"/>.
    /// </summary>
    public long MaxResponseBodySize { get; set; } = DefaultMaxResponseBodySize;

    // ── Partner binding ──────────────────────────────────────────────────────
    /// <summary>Name of an <see cref="As2ConnectionFactory"/> registered in the route context.</summary>
    public string? ConnectionFactory { get; set; }

    /// <summary>
    /// Receive side: name of an <c>IIdempotentRepository</c> registered with <c>context.AddIdempotentRepository(name, repo)</c>
    /// (the contract of the IdempotentConsumer EIP and of the AS4 and S3 receivers). It remembers received
    /// <c>Message-ID</c>s: a message received again is answered with a positive MDN carrying
    /// <c>processed/warning: duplicate-document</c> and not delivered twice. The id is claimed after the message
    /// authenticated and released when the route fails, so the partner's resend is then delivered. Unset (default):
    /// every copy is delivered. Which store it is (memory, redb, SQL) decides whether it survives a restart and works
    /// across nodes; give the endpoint one of its own.
    /// </summary>
    public string? IdempotentRepository { get; set; }

    /// <summary>
    /// Receive side: if true, the exchange body is a <c>Stream</c> (the spooled payload) instead of a byte array, as
    /// <c>streamBody</c> does for the AS4, File, SFTP and S3 consumers; <c>Exchange.DisposeAsync()</c> closes it. Either way
    /// the connector spools the request and each decrypted and decompressed stage to a temporary file past the core
    /// stream-cache threshold, instead of holding them in memory, and hands the payload over only after it is decrypted,
    /// verified and decompressed. Default false.
    /// </summary>
    public bool StreamBody { get; set; }

    /// <summary>What a receive endpoint accepts: business messages (default) or async MDN receipts.</summary>
    public As2ReceiveMode Mode { get; set; } = As2ReceiveMode.Message;

    // ── Agreement (inline; refused when a connection factory is named) ───────
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

    /// <summary>Hard-fail a send on an unacceptable MDN (negative / MIC mismatch / missing required signature).</summary>
    public bool RequireValidMdn { get; set; }

    /// <summary>URL the partner posts asynchronous MDNs to (our receiver). Required for async MDN.</summary>
    public string? AsyncMdnUrl { get; set; }

    /// <summary>
    /// Receive side: comma-separated hosts an asynchronous MDN may be posted to (see
    /// <see cref="As2ConnectionFactory.AsyncMdnAllowedHosts"/>). Required for a receive endpoint whose MDN mode is async.
    /// </summary>
    public string? AsyncMdnAllowedHosts { get; set; }

    // ── Identity ─────────────────────────────────────────────────────────────
    /// <summary>Our AS2 identifier (<c>AS2-From</c> on send, expected <c>AS2-To</c> on receive).</summary>
    public string? As2From { get; set; }

    /// <summary>Partner AS2 identifier (<c>AS2-To</c> on send, expected <c>AS2-From</c> on receive).</summary>
    public string? As2To { get; set; }

    /// <summary>Permits the legacy algorithms <c>sha-1</c> and <c>3des</c> (see <see cref="As2ConnectionFactory.AllowLegacyAlgorithms"/>).</summary>
    public bool AllowLegacyAlgorithms { get; set; }

    /// <inheritdoc />
    public override void Validate()
    {
        if (Port is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(Port), Port, "AS2 port must be between 1 and 65535.");
        if (Timeout < 0)
            throw new ArgumentOutOfRangeException(nameof(Timeout), Timeout, "AS2 timeout must be non-negative.");
        if (MaxRequestBodySize <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaxRequestBodySize), MaxRequestBodySize, "AS2 maxRequestBodySize must be positive.");
        if (MaxResponseBodySize <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaxResponseBodySize), MaxResponseBodySize, "AS2 maxResponseBodySize must be positive.");
        ConcurrencyLimitOptions.ValidateShape(MaxConcurrentRequests, RequestQueueLimit, RejectStatusCode, RetryAfterSeconds);
        if (MdnMode == As2MdnMode.Async && string.IsNullOrEmpty(AsyncMdnUrl) && string.IsNullOrEmpty(ConnectionFactory))
            throw new ArgumentException("Async MDN requires 'asyncMdnUrl' or a 'connectionFactory' that provides it.");
        if (Sign && !Crypto.As2CryptoEngine.IsSupportedDigest(SignAlg))
            throw new ArgumentException($"Unsupported AS2 signature algorithm '{SignAlg}'. Supported: sha-1, sha-256, sha-384, sha-512.");
        if (Encrypt && !Crypto.As2CryptoEngine.IsSupportedEncryption(EncryptAlg))
            throw new ArgumentException($"Unsupported AS2 encryption algorithm '{EncryptAlg}'. Supported: aes-128-cbc, aes-192-cbc, aes-256-cbc, 3des.");
        if (!AllowLegacyAlgorithms && (As2Profile.IsLegacy(SignAlg) || (Encrypt && As2Profile.IsLegacy(EncryptAlg))))
            throw new ArgumentException(
                $"AS2 algorithm '{(As2Profile.IsLegacy(SignAlg) ? SignAlg : EncryptAlg)}' is legacy (sha-1, 3des); set allowLegacyAlgorithms=true only if the partner requires it.");
    }

    /// <inheritdoc />
    protected override string? UnknownParameterHint(string name) =>
        string.Equals(name, "certPassword", StringComparison.OrdinalIgnoreCase)
            ? "The S/MIME certificates and their passwords belong on the As2ConnectionFactory the endpoint names (connectionFactory=...), not in the URI; the TLS certificate is sslCertPath/sslCertPassword."
            : null;
}

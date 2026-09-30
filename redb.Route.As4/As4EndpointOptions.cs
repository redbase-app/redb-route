using redb.Route.Core;
using redb.Route.Http;

namespace redb.Route.As4;

/// <summary>
/// Options of an AS4 endpoint, bound from URI query parameters. Only what may differ per endpoint or per
/// message lives here; the agreement (identities, certificates, algorithms) is the named
/// <see cref="As4ConnectionFactory"/> and its <see cref="As4Partner"/> objects — there is no inline
/// alternative, so one partner has one source of truth.
/// <para>
/// <see cref="Validate"/> checks what the URI alone decides. Everything that depends on the factory is
/// checked when the consumer or producer starts: the registry may be filled after the routes are defined.
/// </para>
/// </summary>
public sealed class As4EndpointOptions : EndpointOptions
{
    /// <summary>Default listen port of a receive endpoint.</summary>
    public const int DefaultPort = 4090;

    /// <summary>Default upper bound on a request body: 100 MB.</summary>
    public const long DefaultMaxRequestBodySize = 100L * 1024 * 1024;

    /// <summary>Default upper bound on the partner's response to a send: 4 MB (a receipt or an error is a few KB).</summary>
    public const long DefaultMaxResponseBodySize = 4L * 1024 * 1024;

    /// <summary>Default upper bound on the SOAP envelope (root MIME part), in characters: 1 M.</summary>
    public const long DefaultMaxEnvelopeCharacters = 1024 * 1024;

    // ── Node and partner ─────────────────────────────────────────────────────

    /// <summary>Registry name of the <see cref="As4ConnectionFactory"/> (our node). Required.</summary>
    public string? ConnectionFactory { get; set; }

    /// <summary>
    /// <see cref="As4Partner.Name"/> of the partner to send to (producer). May be an expression, e.g.
    /// <c>${header.partner}</c>. The name must be one of the node's <see cref="As4ConnectionFactory.Partners"/>.
    /// </summary>
    [EndpointRole(EndpointRole.Producer)]
    public DynamicValue<string>? Partner { get; set; }

    // ── Per-message business values (producer) ───────────────────────────────

    /// <summary>Overrides the agreement's <c>eb:Service</c>; allowed only when the agreement permits it.</summary>
    [EndpointRole(EndpointRole.Producer)]
    public DynamicValue<string>? Service { get; set; }

    /// <summary>Overrides the agreement's <c>eb:Action</c>; allowed only when the agreement permits it.</summary>
    [EndpointRole(EndpointRole.Producer)]
    public DynamicValue<string>? Action { get; set; }

    /// <summary><c>eb:ConversationId</c>. Default: a new id per message, or the <c>redbAs4.conversationId</c> header.</summary>
    [EndpointRole(EndpointRole.Producer)]
    public DynamicValue<string>? ConversationId { get; set; }

    /// <summary>
    /// <c>eb:RefToMessageId</c> — set on the reply of a Two-Way / Push-and-Push exchange. Default: the
    /// <c>redbAs4.refToMessageId</c> header, else none.
    /// </summary>
    [EndpointRole(EndpointRole.Producer)]
    public DynamicValue<string>? RefToMessageId { get; set; }

    /// <summary>Milliseconds to wait for the partner's HTTP response carrying the receipt. Default 60000.</summary>
    [EndpointRole(EndpointRole.Producer)]
    public int Timeout { get; set; } = 60000;

    /// <summary>
    /// Upper bound in bytes on the partner's HTTP response to a send (producer), the counterpart of
    /// <see cref="MaxRequestBodySize"/> on receipt. The response is read as a bounded stream: a larger one (a proxy's
    /// error page, a broken partner) is refused as an invalid receipt without being read into memory.
    /// Default <see cref="DefaultMaxResponseBodySize"/>.
    /// </summary>
    [EndpointRole(EndpointRole.Producer)]
    public long MaxResponseBodySize { get; set; } = DefaultMaxResponseBodySize;

    /// <summary>
    /// Defer the send to the enclosing <c>.Transacted()</c> block. eDelivery requires a synchronous receipt,
    /// so a sender always waits for the response and never defers: <c>true</c> fails when the URI is bound.
    /// </summary>
    [EndpointRole(EndpointRole.Producer)]
    public bool? Transacted { get; set; }

    // ── Receive server (consumer) ────────────────────────────────────────────

    /// <summary>
    /// Name of an <c>IIdempotentRepository</c> registered with <c>context.AddIdempotentRepository(name, repo)</c> —
    /// the contract of the route-level IdempotentConsumer EIP and of the S3 consumer's option of the same name.
    /// It remembers received message ids: a message received again is answered with a receipt and not delivered
    /// twice (AS4 duplicate detection). Required on a receive endpoint — eDelivery AS4 1.16 mandates duplicate
    /// detection, and which store holds the ids (memory, redb, SQL) decides whether it survives a restart and
    /// works across cluster nodes, so it is named explicitly.
    /// <para>
    /// The keys are <c>eb:MessageId</c> as it is, the way Domibus and Holodeck keep them: a message id is globally
    /// unique by the ebMS contract, whoever sends it. Give the endpoint a repository of its own — one shared with
    /// another route's business keys, or reused by a partner's colliding id, would answer a new message with the
    /// receipt of a duplicate and not deliver it.
    /// </para>
    /// </summary>
    [EndpointRole(EndpointRole.Consumer)]
    public string? IdempotentRepository { get; set; }

    /// <summary>
    /// If true, the consumer sets the exchange body of a single-payload message to a <c>Stream</c> instead of a byte
    /// array, as <c>streamBody</c> does for the File, SFTP and S3 consumers. The stream is closed by
    /// <c>Exchange.DisposeAsync()</c>. Either way the connector itself spools a large request and its payloads to a
    /// temporary file (the core stream cache) instead of memory, and hands a payload to the route only after it is
    /// decrypted, its signature verified and it is decompressed. (default: false)
    /// </summary>
    [EndpointRole(EndpointRole.Consumer)]
    public bool StreamBody { get; set; }

    /// <summary>Bind address of the receive server. Default 0.0.0.0.</summary>
    [EndpointRole(EndpointRole.Consumer)]
    public string Host { get; set; } = "0.0.0.0";

    /// <summary>Listen port of the receive server. Default <see cref="DefaultPort"/>.</summary>
    [EndpointRole(EndpointRole.Consumer)]
    public int Port { get; set; } = DefaultPort;

    /// <summary>Serve over HTTPS. Set by the <c>as4s</c> scheme.</summary>
    public bool UseTls { get; set; }

    /// <summary>PFX the receive server presents over TLS; else the factory's. A TLS receiver with neither refuses to start.</summary>
    [EndpointRole(EndpointRole.Consumer)]
    public string? SslCertPath { get; set; }

    /// <summary>Password for <see cref="SslCertPath"/>.</summary>
    [EndpointRole(EndpointRole.Consumer)]
    [Sensitive]
    public string? SslCertPassword { get; set; }

    /// <summary>Whether the receive server asks partners for a TLS client certificate.</summary>
    [EndpointRole(EndpointRole.Consumer)]
    public As4ClientCertificateMode ClientCertificateMode { get; set; } = As4ClientCertificateMode.NoCertificate;

    /// <summary>Thumbprints (comma-separated) an accepted client certificate must match; null: any valid one.</summary>
    [EndpointRole(EndpointRole.Consumer)]
    public string? AllowedClientThumbprints { get; set; }

    /// <summary>Upper bound on a request body in bytes. Default <see cref="DefaultMaxRequestBodySize"/>.</summary>
    [EndpointRole(EndpointRole.Consumer)]
    public long MaxRequestBodySize { get; set; } = DefaultMaxRequestBodySize;

    /// <summary>
    /// Upper bound on the SOAP envelope in characters. Applies to the envelope only — payloads travel as
    /// attachments and are bounded by <see cref="MaxRequestBodySize"/>. Default <see cref="DefaultMaxEnvelopeCharacters"/>.
    /// </summary>
    public long MaxEnvelopeCharacters { get; set; } = DefaultMaxEnvelopeCharacters;

    /// <summary>Maximum concurrent executions of this receiver's pipeline; 0 (default) = unlimited.</summary>
    [EndpointRole(EndpointRole.Consumer)]
    public int MaxConcurrentRequests { get; set; }

    /// <summary>Requests over the limit that wait for a permit (FIFO); 0 (default) = reject at once.</summary>
    [EndpointRole(EndpointRole.Consumer)]
    public int RequestQueueLimit { get; set; }

    /// <summary>Status code of a shed request. Default 503: an AS4 sender retries on it.</summary>
    [EndpointRole(EndpointRole.Consumer)]
    public int RejectStatusCode { get; set; } = 503;

    /// <summary><c>Retry-After</c> of a shed request in seconds; 0 = not sent. Default 1.</summary>
    [EndpointRole(EndpointRole.Consumer)]
    public int RetryAfterSeconds { get; set; } = 1;

    /// <inheritdoc />
    public override void Validate()
    {
        if (string.IsNullOrWhiteSpace(ConnectionFactory))
            throw new ArgumentException("AS4 endpoint requires 'connectionFactory': the registry name of an As4ConnectionFactory.");
        if (Port is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(Port), Port, "AS4 port must be between 1 and 65535.");
        if (Timeout < 0)
            throw new ArgumentOutOfRangeException(nameof(Timeout), Timeout, "AS4 timeout must not be negative.");
        if (MaxRequestBodySize <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaxRequestBodySize), MaxRequestBodySize, "AS4 maxRequestBodySize must be positive.");
        if (MaxResponseBodySize <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaxResponseBodySize), MaxResponseBodySize, "AS4 maxResponseBodySize must be positive.");
        if (MaxEnvelopeCharacters <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaxEnvelopeCharacters), MaxEnvelopeCharacters, "AS4 maxEnvelopeCharacters must be positive.");
        // eDelivery AS4 1.16 fixes the receipt to the HTTP response of the same request, so a sender always waits for it:
        // the URI alone decides this, so it fails in Validate as RabbitMQ refuses ackMode=auto with transacted.
        if (Transacted == true)
            throw new ArgumentException("AS4 transacted=true is not supported: an AS4 send waits for its receipt in the response " +
                "and cannot be deferred to the end of a transaction.");
        if (ClientCertificateMode != As4ClientCertificateMode.NoCertificate && !UseTls)
            throw new ArgumentException("AS4 clientCertificateMode needs TLS: a client certificate is presented in the TLS handshake. Use the as4s scheme.");
        ConcurrencyLimitOptions.ValidateShape(MaxConcurrentRequests, RequestQueueLimit, RejectStatusCode, RetryAfterSeconds);
    }
}

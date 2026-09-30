namespace redb.Route.As4;

/// <summary>
/// Exchange header names of the AS4 connector. Everything is under <see cref="Prefix"/>: AS4 carries its
/// business metadata inside <c>eb:Messaging</c>, not in HTTP headers, so there is no verbatim wire plane to
/// mirror.
/// <para>
/// Canonical planes (one source of truth per value, consumer and producer agree):
/// <list type="bullet">
///   <item><description><b>HTTP</b> — transport only; not copied onto the exchange, except the caller's
///   address (<see cref="RemoteAddress"/>) and <c>traceparent</c> (into the span).</description></item>
///   <item><description><b>SOAP header</b> — <c>wsse:Security</c> is consumed by the connector; its outcome
///   is <see cref="SignatureValid"/> and <see cref="SignerThumbprint"/>.</description></item>
///   <item><description><b><c>eb:Messaging</c></b> — the main plane: <see cref="MessageId"/> …
///   <see cref="PModeId"/>. On send, only these names are read, by an explicit list; nothing else from the
///   exchange reaches the partner's archive.</description></item>
///   <item><description><b><c>eb:MessageProperties</c></b> — <see cref="PropertyPrefix"/> + property name,
///   both ways.</description></item>
///   <item><description><b>MIME part</b> — per-payload properties travel on the payload object; the
///   exchange's <c>Message.ContentType</c> is the original (decompressed) <c>MimeType</c>.</description></item>
/// </list>
/// </para>
/// </summary>
public static class As4Headers
{
    /// <summary>Prefix of every AS4 header on the exchange.</summary>
    public const string Prefix = "redbAs4.";

    // ── eb:Messaging plane ───────────────────────────────────────────────────

    /// <summary><c>eb:MessageInfo/eb:MessageId</c>. Set by the producer before the first attempt, so a redelivery resends the same id.</summary>
    public const string MessageId = Prefix + "messageId";

    /// <summary><c>eb:MessageInfo/eb:RefToMessageId</c>.</summary>
    public const string RefToMessageId = Prefix + "refToMessageId";

    /// <summary><c>eb:MessageInfo/eb:Timestamp</c> of a received message.</summary>
    public const string Timestamp = Prefix + "timestamp";

    /// <summary><c>eb:CollaborationInfo/eb:ConversationId</c>.</summary>
    public const string ConversationId = Prefix + "conversationId";

    /// <summary>Sender <c>eb:PartyId</c>.</summary>
    public const string FromPartyId = Prefix + "fromPartyId";

    /// <summary>Sender <c>eb:PartyId/@type</c>.</summary>
    public const string FromPartyIdType = Prefix + "fromPartyIdType";

    /// <summary>Sender <c>eb:Role</c>.</summary>
    public const string FromRole = Prefix + "fromRole";

    /// <summary>Recipient <c>eb:PartyId</c>.</summary>
    public const string ToPartyId = Prefix + "toPartyId";

    /// <summary>Recipient <c>eb:PartyId/@type</c>.</summary>
    public const string ToPartyIdType = Prefix + "toPartyIdType";

    /// <summary>Recipient <c>eb:Role</c>.</summary>
    public const string ToRole = Prefix + "toRole";

    /// <summary><c>eb:CollaborationInfo/eb:Service</c>.</summary>
    public const string Service = Prefix + "service";

    /// <summary><c>eb:Service/@type</c>.</summary>
    public const string ServiceType = Prefix + "serviceType";

    /// <summary><c>eb:CollaborationInfo/eb:Action</c>.</summary>
    public const string Action = Prefix + "action";

    /// <summary><c>eb:CollaborationInfo/eb:AgreementRef</c>.</summary>
    public const string AgreementRef = Prefix + "agreementRef";

    /// <summary><c>eb:AgreementRef/@pmode</c>.</summary>
    public const string PModeId = Prefix + "pmode";

    // ── eb:MessageProperties plane ───────────────────────────────────────────

    /// <summary>Prefix of message properties: <c>redbAs4.property.originalSender</c> is <c>eb:Property[@name='originalSender']</c>.</summary>
    public const string PropertyPrefix = Prefix + "property.";

    /// <summary>The eDelivery four-corner <c>originalSender</c> property (mandatory).</summary>
    public const string OriginalSender = PropertyPrefix + "originalSender";

    /// <summary>The eDelivery four-corner <c>finalRecipient</c> property (mandatory).</summary>
    public const string FinalRecipient = PropertyPrefix + "finalRecipient";

    // ── Computed by the connector ────────────────────────────────────────────

    /// <summary>Registry name of the partner the message was matched to (receive) or sent to (send).</summary>
    public const string Partner = Prefix + "partner";

    /// <summary>True when the signature was present and verified against the partner's certificate.</summary>
    public const string SignatureValid = Prefix + "signatureValid";

    /// <summary>Thumbprint of the certificate that signed the received message.</summary>
    public const string SignerThumbprint = Prefix + "signerThumbprint";

    /// <summary>Address of the peer that sent the request.</summary>
    public const string RemoteAddress = Prefix + "remoteAddress";

    /// <summary>Thumbprint of the TLS client certificate the partner presented (mutual TLS).</summary>
    public const string ClientCertThumbprint = Prefix + "clientCertThumbprint";

    /// <summary>Subject of the TLS client certificate.</summary>
    public const string ClientCertSubject = Prefix + "clientCertSubject";

    /// <summary>Expiry of the TLS client certificate, UTC, round-trip format.</summary>
    public const string ClientCertNotAfter = Prefix + "clientCertNotAfter";

    /// <summary>True when a receipt was received and verified, including its non-repudiation references (send).</summary>
    public const string ReceiptValid = Prefix + "receiptValid";

    /// <summary><c>eb:MessageId</c> of the receipt signal (send).</summary>
    public const string ReceiptMessageId = Prefix + "receiptMessageId";

    /// <summary>True if <paramref name="key"/> is an AS4 connector header.</summary>
    public static bool IsRedbHeader(string key) => key.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);
}

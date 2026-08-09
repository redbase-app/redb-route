namespace redb.Route.As2;

/// <summary>
/// AS2 header names surfaced on the exchange, and the reserved set that must not be blindly bridged.
/// <para>
/// Two planes, deliberately: AS2 wire headers keep their <b>verbatim</b> names (<c>AS2-From</c>, ...),
/// while redb-computed metadata (MIC, signature validity, ...) lives under the <see cref="Prefix"/>.
/// Canonical-plane rule (from the IBM MQ usr-folder decision): each header has ONE source of truth and the
/// consumer and producer never disagree. The load-bearing example is <c>Content-Type</c> — the transport
/// value is the <c>multipart/signed</c> wrapper; the business payload's real content type lives on
/// <c>Message.ContentType</c>. See <c>docs/as2/02-DESIGN.md §8</c>.
/// </para>
/// </summary>
public static class As2Headers
{
    /// <summary>Prefix for redb-computed AS2 metadata headers.</summary>
    public const string Prefix = "redbAs2.";

    // ── AS2 wire headers (verbatim names; constants for explicit set/get) ────
    /// <summary>Sender AS2 identifier.</summary>
    public const string As2From = "AS2-From";
    /// <summary>Recipient AS2 identifier.</summary>
    public const string As2To = "AS2-To";
    /// <summary>AS2 protocol version.</summary>
    public const string As2Version = "AS2-Version";
    /// <summary>Unique message id; echoed back in the MDN as Original-Message-ID.</summary>
    public const string MessageId = "Message-ID";
    /// <summary>Human-readable subject.</summary>
    public const string Subject = "Subject";
    /// <summary>Requests an MDN and (for async) where to send it.</summary>
    public const string DispositionNotificationTo = "Disposition-Notification-To";
    /// <summary>Requested MDN profile (signed-receipt protocol / micalg).</summary>
    public const string DispositionNotificationOptions = "Disposition-Notification-Options";
    /// <summary>URL for an asynchronous MDN (present ⇒ async).</summary>
    public const string ReceiptDeliveryOption = "Receipt-Delivery-Option";

    // ── redb-computed metadata (under Prefix) ────────────────────────────────
    /// <summary>Computed Message Integrity Check value.</summary>
    public const string Mic = Prefix + "mic";
    /// <summary>MIC digest algorithm.</summary>
    public const string MicAlg = Prefix + "micalg";
    /// <summary>Resolved partner (connection-factory) name.</summary>
    public const string PartnerName = Prefix + "partner";
    /// <summary>Whether the inbound signature verified.</summary>
    public const string SignatureValid = Prefix + "signatureValid";
    /// <summary>Remote peer address.</summary>
    public const string RemoteAddress = Prefix + "remoteAddress";
    /// <summary>MDN disposition outcome.</summary>
    public const string MdnDisposition = Prefix + "mdnDisposition";
    /// <summary>Whether the MDN's Received-Content-MIC matched what we sent.</summary>
    public const string MdnMicMatch = Prefix + "mdnMicMatch";

    /// <summary>True if the header name is a redb AS2 metadata header (must not be bridged onto the wire).</summary>
    public static bool IsRedbHeader(string key)
        => key.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Headers the producer must NOT blindly copy from <c>exchange.In.Headers</c> onto the outgoing message:
    /// MIME headers we set explicitly through the content object, AS2 semantics we set from partner config,
    /// and HTTP hop-by-hop headers. redb metadata is filtered separately via <see cref="IsRedbHeader"/>.
    /// </summary>
    internal static readonly HashSet<string> NonBridgedHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        // MIME headers set explicitly via the content object
        "Content-Type", "Content-Disposition", "Content-Transfer-Encoding", "Content-Length", "Content-Encoding",
        // AS2 semantics set from partner config
        "AS2-From", "AS2-To", "AS2-Version", "Message-ID",
        "Disposition-Notification-To", "Disposition-Notification-Options", "Receipt-Delivery-Option",
        // HTTP hop-by-hop
        "Connection", "Keep-Alive", "Transfer-Encoding", "TE", "Trailer", "Upgrade",
        "Proxy-Authorization", "Proxy-Authenticate",
    };
}

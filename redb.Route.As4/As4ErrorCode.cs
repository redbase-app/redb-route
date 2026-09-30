namespace redb.Route.As4;

/// <summary>Severity of an ebMS error (ebMS 3.0 Core §6.3).</summary>
public enum As4ErrorSeverity
{
    /// <summary>The message unit in error is not processed further.</summary>
    Failure,

    /// <summary>Processing continued; the partner is only informed.</summary>
    Warning,
}

/// <summary>
/// An ebMS 3.0 error code (Core §6.7, AS4 profile §3) with its short description, category and default
/// severity, and the fixed text we send to a partner for it. A received message never gets the text of one of
/// our exceptions back (BR-4): that text names our files, connections and services; the partner gets this
/// description and a reference to quote.
/// </summary>
public sealed record As4ErrorCode(string Code, string ShortDescription, string Category, As4ErrorSeverity Severity, string PartnerText)
{
    /// <summary>EBMS:0001 — a value of the message is not recognized (unknown agreement, party, service, action).</summary>
    public static readonly As4ErrorCode ValueNotRecognized = new("EBMS:0001", "ValueNotRecognized", "Content", As4ErrorSeverity.Failure,
        "A value of the message header is not recognized for any agreement.");

    /// <summary>EBMS:0002 — a feature the message asks for is not supported.</summary>
    public static readonly As4ErrorCode FeatureNotSupported = new("EBMS:0002", "FeatureNotSupported", "Content", As4ErrorSeverity.Warning,
        "A feature the message asks for is not supported.");

    /// <summary>EBMS:0003 — values of the message contradict each other or the agreement.</summary>
    public static readonly As4ErrorCode ValueInconsistent = new("EBMS:0003", "ValueInconsistent", "Content", As4ErrorSeverity.Failure,
        "Values of the message are inconsistent.");

    /// <summary>EBMS:0004 — any other failure; for us, a failure of the receiving route.</summary>
    public static readonly As4ErrorCode Other = new("EBMS:0004", "Other", "Content", As4ErrorSeverity.Failure,
        "The message could not be processed.");

    /// <summary>EBMS:0005 — the receiving MSH could not be reached (reported locally by a sender). Not raised by this connector: a send that cannot connect is an HttpRequestException the route handles.</summary>
    public static readonly As4ErrorCode ConnectionFailure = new("EBMS:0005", "ConnectionFailure", "Communication", As4ErrorSeverity.Failure,
        "The receiving MSH could not be reached.");

    /// <summary>EBMS:0006 — a pulled partition channel is empty (pull only). Reserved for pull (phase 10, out of scope).</summary>
    public static readonly As4ErrorCode EmptyMessagePartitionChannel = new("EBMS:0006", "EmptyMessagePartitionChannel", "Communication", As4ErrorSeverity.Warning,
        "The message partition channel holds no message.");

    /// <summary>EBMS:0007 — the MIME structure does not match the header's payload references.</summary>
    public static readonly As4ErrorCode MimeInconsistency = new("EBMS:0007", "MimeInconsistency", "Unpackaging", As4ErrorSeverity.Failure,
        "The MIME parts do not match the payload references of the message header.");

    /// <summary>EBMS:0009 — the ebMS header is missing or malformed.</summary>
    public static readonly As4ErrorCode InvalidHeader = new("EBMS:0009", "InvalidHeader", "Unpackaging", As4ErrorSeverity.Failure,
        "The ebMS header is missing or malformed.");

    /// <summary>EBMS:0010 — the message does not match the processing mode of its agreement.</summary>
    public static readonly As4ErrorCode ProcessingModeMismatch = new("EBMS:0010", "ProcessingModeMismatch", "Processing", As4ErrorSeverity.Failure,
        "The message does not match the processing mode of its agreement.");

    /// <summary>EBMS:0011 — a payload referenced outside the message could not be used.</summary>
    public static readonly As4ErrorCode ExternalPayloadError = new("EBMS:0011", "ExternalPayloadError", "Content", As4ErrorSeverity.Failure,
        "Payloads outside the message are not supported.");

    /// <summary>EBMS:0101 — the signature (or anything it protects) did not verify.</summary>
    public static readonly As4ErrorCode FailedAuthentication = new("EBMS:0101", "FailedAuthentication", "Processing", As4ErrorSeverity.Failure,
        "The security check of the message failed.");

    /// <summary>EBMS:0102 — the message could not be decrypted.</summary>
    public static readonly As4ErrorCode FailedDecryption = new("EBMS:0102", "FailedDecryption", "Processing", As4ErrorSeverity.Failure,
        "The security check of the message failed.");

    /// <summary>EBMS:0103 — the message is not secured the way the agreement requires.</summary>
    public static readonly As4ErrorCode PolicyNoncompliance = new("EBMS:0103", "PolicyNoncompliance", "Processing", As4ErrorSeverity.Failure,
        "The message is not secured as the agreement requires.");

    /// <summary>EBMS:0201 — reliability processing failed. Not raised: reliability is the engine's redelivery.</summary>
    public static readonly As4ErrorCode DysfunctionalReliability = new("EBMS:0201", "DysfunctionalReliability", "Processing", As4ErrorSeverity.Failure,
        "Reliable messaging processing failed.");

    /// <summary>EBMS:0202 — the message could not be delivered to its consumer. Not raised today: a route failure is answered EBMS:0004 (docs/as4/REVIEW-2026-09-28.md, R12).</summary>
    public static readonly As4ErrorCode DeliveryFailure = new("EBMS:0202", "DeliveryFailure", "Communication", As4ErrorSeverity.Failure,
        "The message could not be delivered.");

    /// <summary>EBMS:0301 — no receipt arrived for a message that required one (sender side).</summary>
    public static readonly As4ErrorCode MissingReceipt = new("EBMS:0301", "MissingReceipt", "Communication", As4ErrorSeverity.Failure,
        "No receipt was received for the message.");

    /// <summary>EBMS:0302 — a receipt arrived but does not prove receipt of what was sent (sender side).</summary>
    public static readonly As4ErrorCode InvalidReceipt = new("EBMS:0302", "InvalidReceipt", "Communication", As4ErrorSeverity.Failure,
        "The receipt does not match the message.");

    /// <summary>EBMS:0303 — a compressed payload could not be decompressed.</summary>
    public static readonly As4ErrorCode DecompressionFailure = new("EBMS:0303", "DecompressionFailure", "Communication", As4ErrorSeverity.Failure,
        "A payload could not be decompressed.");

    private static readonly IReadOnlyDictionary<string, As4ErrorCode> ByCode = new[]
    {
        ValueNotRecognized, FeatureNotSupported, ValueInconsistent, Other, ConnectionFailure, EmptyMessagePartitionChannel,
        MimeInconsistency, InvalidHeader, ProcessingModeMismatch, ExternalPayloadError, FailedAuthentication, FailedDecryption,
        PolicyNoncompliance, DysfunctionalReliability, DeliveryFailure, MissingReceipt, InvalidReceipt, DecompressionFailure,
    }.ToDictionary(c => c.Code, StringComparer.Ordinal);

    /// <summary>The known code named <paramref name="code"/> (<c>EBMS:0009</c>), or null for a code we do not know.</summary>
    public static As4ErrorCode? Find(string? code) => code is not null && ByCode.TryGetValue(code, out var known) ? known : null;
}

namespace redb.Route.IbmMq;

/// <summary>
/// Well-known header constants used by the IBM MQ component.
/// Headers prefixed with <c>redbIbmMq.</c> carry broker metadata on incoming messages.
/// </summary>
public static class IbmMqHeaders
{
    /// <summary>Common prefix for all IBM MQ component headers.</summary>
    public const string Prefix = "redbIbmMq.";

    // ── MQMD fields set on the In message by the consumer ────────────

    /// <summary>Queue or topic the message was received from.</summary>
    public const string Destination = "redbIbmMq.Destination";

    /// <summary>Queue manager name.</summary>
    public const string QueueManager = "redbIbmMq.QueueManager";

    /// <summary>MQMD MsgId (hex string).</summary>
    public const string MsgId = "redbIbmMq.MsgId";

    /// <summary>MQMD CorrelId (hex string).</summary>
    public const string CorrelId = "redbIbmMq.CorrelId";

    /// <summary>MQMD Format (e.g. MQFMT_STRING, MQFMT_NONE, MQHRF2).</summary>
    public const string Format = "redbIbmMq.Format";

    /// <summary>MQMD Coded Character Set ID.</summary>
    public const string CCSID = "redbIbmMq.CCSID";

    /// <summary>MQMD Encoding.</summary>
    public const string Encoding = "redbIbmMq.Encoding";

    /// <summary>MQMD Persistence.</summary>
    public const string Persistence = "redbIbmMq.Persistence";

    /// <summary>MQMD Priority.</summary>
    public const string Priority = "redbIbmMq.Priority";

    /// <summary>MQMD Expiry (tenths of a second, -1 = unlimited).</summary>
    public const string Expiry = "redbIbmMq.Expiry";

    /// <summary>MQMD ReplyToQ.</summary>
    public const string ReplyToQueue = "redbIbmMq.ReplyToQueue";

    /// <summary>MQMD ReplyToQMgr.</summary>
    public const string ReplyToQueueManager = "redbIbmMq.ReplyToQueueManager";

    /// <summary>MQMD MsgType (Datagram, Request, Reply, Report).</summary>
    public const string MsgType = "redbIbmMq.MsgType";

    /// <summary>MQMD PutApplName.</summary>
    public const string PutApplName = "redbIbmMq.PutApplName";

    /// <summary>MQMD PutApplType.</summary>
    public const string PutApplType = "redbIbmMq.PutApplType";

    /// <summary>MQMD GroupId (hex string).</summary>
    public const string GroupId = "redbIbmMq.GroupId";

    /// <summary>MQMD MsgSeqNumber within group.</summary>
    public const string MsgSeqNumber = "redbIbmMq.MsgSeqNumber";

    /// <summary>MQMD Feedback code.</summary>
    public const string Feedback = "redbIbmMq.Feedback";

    /// <summary>MQMD Report options.</summary>
    public const string ReportOptions = "redbIbmMq.ReportOptions";

    /// <summary>MQMD UserIdentifier.</summary>
    public const string UserIdentifier = "redbIbmMq.UserIdentifier";

    /// <summary>MQMD BackoutCount.</summary>
    public const string BackoutCount = "redbIbmMq.BackoutCount";

    /// <summary>MQMD PutDateTime.</summary>
    public const string PutDateTime = "redbIbmMq.PutDateTime";

    // ── Internal property carrying the user-header catalogue ────────────

    /// <summary>
    /// Name of the MQ message property that carries all user headers as a single JSON object.
    /// <para>
    /// Bundling into one property is necessary because MQ property names forbid hyphens, so HTTP-style
    /// header names (<c>X-Custom-Id</c>) can't be individual properties. The name is <b>unqualified</b>
    /// (no dotted folder prefix) so it lands in the JMS <c>usr</c> folder — the standard, interoperable
    /// user-property location that both the IBM.WMQ (poll) and IBM.XMS (listener) clients, and any other
    /// JMS client, read and write. (A dotted name would create a custom MQRFH2 folder that JMS clients,
    /// including XMS, do not surface as user properties.)
    /// </para>
    /// </summary>
    internal const string HeaderCatalogue = "redbIbmMqHeaders";

    /// <summary>Returns true if the header key belongs to the IBM MQ component.</summary>
    public static bool IsRedbHeader(string key) =>
        key.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);
}

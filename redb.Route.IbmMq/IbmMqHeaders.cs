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

    // ── Internal property for header key catalogue ────────────

    /// <summary>Comma-delimited list of custom header keys stored by the producer.</summary>
    internal const string HeaderKeys = "redbIbmMq.HeaderKeys";

    /// <summary>Returns true if the header key belongs to the IBM MQ component.</summary>
    public static bool IsRedbHeader(string key) =>
        key.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);
}

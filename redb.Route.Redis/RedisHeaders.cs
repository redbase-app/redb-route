namespace redb.Route.Redis;

/// <summary>
/// Well-known header constants used by the Redis component.
/// </summary>
public static class RedisHeaders
{
    /// <summary>Common prefix for all Redis component headers.</summary>
    public const string Prefix = "redbRedis.";

    // ── Consumer headers ─────────────────────────────────────────────

    /// <summary>PubSub channel name.</summary>
    public const string Channel = "redbRedis.Channel";

    /// <summary>Message type: "PubSub", "Stream", or "List".</summary>
    public const string MessageType = "redbRedis.MessageType";

    /// <summary>Timestamp when the message was received.</summary>
    public const string Timestamp = "redbRedis.Timestamp";

    /// <summary>Stream name (stream mode).</summary>
    public const string Stream = "redbRedis.Stream";

    /// <summary>Stream entry ID (stream mode).</summary>
    public const string MessageId = "redbRedis.MessageId";

    /// <summary>List key name (list mode).</summary>
    public const string Key = "redbRedis.Key";

    /// <summary>Prefix for stream field values. Example: "redbRedis.Stream.fieldName".</summary>
    public const string StreamFieldPrefix = "redbRedis.Stream.";

    // ── Producer headers (set after operation) ───────────────────────

    /// <summary>Number of recipients that received a PubSub publish.</summary>
    public const string PublishRecipients = "redbRedis.Publish.Recipients";

    /// <summary>Message ID returned after XADD to a stream.</summary>
    public const string StreamMessageId = "redbRedis.Stream.MessageId";

    // ── Producer input headers (set by user before send) ─────────────

    /// <summary>Custom Redis command name.</summary>
    public const string Command = "redbRedis.Command";

    /// <summary>Hash fields dictionary for HSET/HMSET operations.</summary>
    public const string HashFields = "redbRedis.HashFields";

    /// <summary>Field names array for HMGET operations.</summary>
    public const string FieldNames = "redbRedis.FieldNames";

    /// <summary>Custom command arguments array.</summary>
    public const string CommandArgs = "redbRedis.CommandArgs";

    /// <summary>Source keys array for PFMERGE operations.</summary>
    public const string SourceKeys = "redbRedis.SourceKeys";

    /// <summary>Center longitude for GeoRadius operations.</summary>
    public const string CenterLongitude = "redbRedis.CenterLongitude";

    /// <summary>Center latitude for GeoRadius operations.</summary>
    public const string CenterLatitude = "redbRedis.CenterLatitude";

    /// <summary>Radius value for GeoRadius operations.</summary>
    public const string Radius = "redbRedis.Radius";

    /// <summary>Radius unit for GeoRadius operations (m, km, mi, ft).</summary>
    public const string RadiusUnit = "redbRedis.RadiusUnit";

    /// <summary>Bit offset for SETBIT/GETBIT operations.</summary>
    public const string BitOffset = "redbRedis.Offset";

    /// <summary>Bit value for SETBIT operations.</summary>
    public const string Bit = "redbRedis.Bit";

    /// <summary>Returns true if the header key belongs to the Redis component.</summary>
    public static bool IsRedbHeader(string key) =>
        key.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);
}

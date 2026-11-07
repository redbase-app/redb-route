namespace redb.Route.Kafka;

/// <summary>
/// Well-known header constants used by the Kafka component.
/// </summary>
public static class KafkaHeaders
{
    /// <summary>Common prefix for all Kafka component headers.</summary>
    public const string Prefix = "redbKafka.";

    // ── Headers set on the In message by the consumer ────────────────

    /// <summary>Kafka topic of the consumed record.</summary>
    public const string Topic = "redbKafka.Topic";

    /// <summary>Partition the record was consumed from.</summary>
    public const string Partition = "redbKafka.Partition";

    /// <summary>Offset of the consumed record in the partition.</summary>
    public const string Offset = "redbKafka.Offset";

    /// <summary>Timestamp of the consumed record.</summary>
    public const string Timestamp = "redbKafka.Timestamp";

    /// <summary>Key of the consumed record (if present).</summary>
    public const string Key = "redbKafka.Key";

    /// <summary>Number of records in the batch (batch mode only).</summary>
    public const string BatchSize = "redbKafka.BatchSize";

    // ── Headers set on the In message by the producer after send ─────

    /// <summary>Kafka topic the record was sent to.</summary>
    public const string SentTopic = "redbKafka.Sent.Topic";

    /// <summary>Partition the record was sent to.</summary>
    public const string SentPartition = "redbKafka.Sent.Partition";

    /// <summary>Offset assigned to the sent record.</summary>
    public const string SentOffset = "redbKafka.Sent.Offset";

    /// <summary>Timestamp of the sent record.</summary>
    public const string SentTimestamp = "redbKafka.Sent.Timestamp";

    /// <summary>Returns true if the header key belongs to the Kafka component.</summary>
    public static bool IsRedbHeader(string key) =>
        key.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);
}

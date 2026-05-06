namespace redb.Route.AzureServiceBus;

/// <summary>
/// Header constants for Azure Service Bus connector. Prefix: <c>redbAsb.</c>
/// Maps to <c>ServiceBusMessage</c> / <c>ServiceBusReceivedMessage</c> properties.
/// </summary>
public static class AzureServiceBusHeaders
{
    /// <summary>Header prefix for all Azure Service Bus headers.</summary>
    public const string Prefix = "redbAsb.";

    // ── Message identity ──

    /// <summary>Unique message identifier.</summary>
    public const string MessageId = "redbAsb.MessageId";

    /// <summary>Correlation identifier for request-reply.</summary>
    public const string CorrelationId = "redbAsb.CorrelationId";

    /// <summary>Session identifier (FIFO ordering key).</summary>
    public const string SessionId = "redbAsb.SessionId";

    /// <summary>Partition key for batching within a partition.</summary>
    public const string PartitionKey = "redbAsb.PartitionKey";

    /// <summary>Reply-to session identifier for RPC over sessions.</summary>
    public const string ReplyToSessionId = "redbAsb.ReplyToSessionId";

    // ── Message metadata ──

    /// <summary>Message subject (label).</summary>
    public const string Subject = "redbAsb.Subject";

    /// <summary>Content type of the message body.</summary>
    public const string ContentType = "redbAsb.ContentType";

    /// <summary>Reply-to address for RPC.</summary>
    public const string ReplyTo = "redbAsb.ReplyTo";

    /// <summary>Destination address (informational).</summary>
    public const string To = "redbAsb.To";

    /// <summary>Message time-to-live as TimeSpan string.</summary>
    public const string TimeToLive = "redbAsb.TimeToLive";

    // ── Scheduling ──

    /// <summary>Scheduled enqueue time (DateTimeOffset).</summary>
    public const string ScheduledEnqueueTime = "redbAsb.ScheduledEnqueueTime";

    /// <summary>Sequence number assigned by the broker.</summary>
    public const string SequenceNumber = "redbAsb.SequenceNumber";

    // ── Consumer-only (received message metadata) ──

    /// <summary>Number of delivery attempts.</summary>
    public const string DeliveryCount = "redbAsb.DeliveryCount";

    /// <summary>Time the message was enqueued (DateTimeOffset).</summary>
    public const string EnqueuedTime = "redbAsb.EnqueuedTime";

    /// <summary>Time the message expires (DateTimeOffset).</summary>
    public const string ExpiresAt = "redbAsb.ExpiresAt";

    /// <summary>Time the lock expires (DateTimeOffset, PeekLock only).</summary>
    public const string LockedUntil = "redbAsb.LockedUntil";

    /// <summary>Lock token string (PeekLock only).</summary>
    public const string LockToken = "redbAsb.LockToken";

    // ── Dead-letter ──

    /// <summary>Original entity that dead-lettered the message.</summary>
    public const string DeadLetterSource = "redbAsb.DeadLetterSource";

    /// <summary>Reason the message was dead-lettered.</summary>
    public const string DeadLetterReason = "redbAsb.DeadLetterReason";

    /// <summary>Error description when dead-lettered.</summary>
    public const string DeadLetterErrorDescription = "redbAsb.DeadLetterErrorDescription";

    // ── Session state ──

    /// <summary>Session state (BinaryData serialized).</summary>
    public const string SessionState = "redbAsb.SessionState";

    // ── Batch producer ──

    /// <summary>Number of messages in a sent batch.</summary>
    public const string BatchMessageCount = "redbAsb.BatchMessageCount";

    /// <summary>Checks if a header key belongs to the Azure Service Bus connector.</summary>
    public static bool IsAsbHeader(string key)
        => key.StartsWith(Prefix, StringComparison.Ordinal);
}

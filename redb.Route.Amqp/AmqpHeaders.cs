namespace redb.Route.Amqp;

/// <summary>
/// Well-known header constants used by the AMQP 1.0 component.
/// Headers prefixed with <c>redbAmqp.</c> carry broker metadata on incoming messages.
/// </summary>
public static class AmqpHeaders
{
    /// <summary>Common prefix for all AMQP component headers.</summary>
    public const string Prefix = "redbAmqp.";

    // ── Headers set on the In message by the consumer ────────────────

    /// <summary>AMQP address the message was received from.</summary>
    public const string Address = "redbAmqp.Address";

    /// <summary>AMQP message-id property.</summary>
    public const string MessageId = "redbAmqp.MessageId";

    /// <summary>AMQP correlation-id property.</summary>
    public const string CorrelationId = "redbAmqp.CorrelationId";

    /// <summary>AMQP reply-to property.</summary>
    public const string ReplyTo = "redbAmqp.ReplyTo";

    /// <summary>AMQP content-type property.</summary>
    public const string ContentType = "redbAmqp.ContentType";

    /// <summary>AMQP subject property.</summary>
    public const string Subject = "redbAmqp.Subject";

    /// <summary>AMQP group-id property.</summary>
    public const string GroupId = "redbAmqp.GroupId";

    /// <summary>AMQP group-sequence property.</summary>
    public const string GroupSequence = "redbAmqp.GroupSequence";

    /// <summary>AMQP creation-time property.</summary>
    public const string CreationTime = "redbAmqp.CreationTime";

    /// <summary>AMQP absolute-expiry-time property.</summary>
    public const string AbsoluteExpiryTime = "redbAmqp.AbsoluteExpiryTime";

    /// <summary>Whether the message is durable (header.durable).</summary>
    public const string Durable = "redbAmqp.Durable";

    /// <summary>Message priority (header.priority).</summary>
    public const string Priority = "redbAmqp.Priority";

    /// <summary>TTL in milliseconds (header.ttl).</summary>
    public const string Ttl = "redbAmqp.Ttl";

    /// <summary>Delivery count (header.delivery-count).</summary>
    public const string DeliveryCount = "redbAmqp.DeliveryCount";

    /// <summary>Whether first-acquirer flag was set.</summary>
    public const string FirstAcquirer = "redbAmqp.FirstAcquirer";

    /// <summary>Sender settle mode used for this link.</summary>
    public const string SenderSettleMode = "redbAmqp.SenderSettleMode";

    /// <summary>Receiver settle mode used for this link.</summary>
    public const string ReceiverSettleMode = "redbAmqp.ReceiverSettleMode";

    /// <summary>Returns true if the header key belongs to the AMQP component.</summary>
    public static bool IsRedbHeader(string key) =>
        key.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);
}

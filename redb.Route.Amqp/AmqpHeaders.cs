namespace redb.Route.Amqp;

/// <summary>
/// Well-known header constants used by the AMQP 1.0 component.
/// Headers prefixed with <c>redbAmqp.</c> carry broker metadata on incoming messages.
/// </summary>
public static class AmqpHeaders
{
    /// <summary>Common prefix for redb-specific AMQP envelope headers (transport metadata).</summary>
    public const string Prefix = "redbAmqp.";

    // ── Standard AMQP 1.0 properties (well-known names → BARE, self-documenting) ──
    // Sections `Properties` and `Header`. Bare names mean no docs needed and a
    // consume→produce round-trip carries them through — same convention as the RabbitMQ
    // component. Caveat: common-word names (Subject/Priority/…) can collide with a
    // business header of the same name.

    /// <summary>AMQP message-id property.</summary>
    public const string MessageId = "MessageId";

    /// <summary>AMQP correlation-id property.</summary>
    public const string CorrelationId = "CorrelationId";

    /// <summary>AMQP reply-to property.</summary>
    public const string ReplyTo = "ReplyTo";

    /// <summary>AMQP content-type property.</summary>
    public const string ContentType = "ContentType";

    /// <summary>AMQP content-encoding property.</summary>
    public const string ContentEncoding = "ContentEncoding";

    /// <summary>AMQP subject property.</summary>
    public const string Subject = "Subject";

    /// <summary>AMQP to property.</summary>
    public const string To = "To";

    /// <summary>AMQP user-id property (binary on the wire).</summary>
    public const string UserId = "UserId";

    /// <summary>AMQP group-id property.</summary>
    public const string GroupId = "GroupId";

    /// <summary>AMQP group-sequence property.</summary>
    public const string GroupSequence = "GroupSequence";

    /// <summary>AMQP reply-to-group-id property.</summary>
    public const string ReplyToGroupId = "ReplyToGroupId";

    /// <summary>AMQP creation-time property.</summary>
    public const string CreationTime = "CreationTime";

    /// <summary>AMQP absolute-expiry-time property.</summary>
    public const string AbsoluteExpiryTime = "AbsoluteExpiryTime";

    /// <summary>Whether the message is durable (header.durable).</summary>
    public const string Durable = "Durable";

    /// <summary>Message priority (header.priority).</summary>
    public const string Priority = "Priority";

    /// <summary>TTL in milliseconds (header.ttl).</summary>
    public const string Ttl = "Ttl";

    // ── Envelope / transport metadata (no standard property name → redb-prefixed, read-only) ──

    /// <summary>AMQP address the message was received from.</summary>
    public const string Address = "redbAmqp.Address";

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

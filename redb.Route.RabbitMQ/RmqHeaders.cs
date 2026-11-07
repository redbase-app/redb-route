namespace redb.Route.RabbitMQ;

/// <summary>
/// Well-known header constants used by the RabbitMQ component.
/// </summary>
public static class RmqHeaders
{
    /// <summary>Common prefix for redb-specific RabbitMQ envelope headers (delivery metadata).</summary>
    public const string Prefix = "redbRmq.";

    // ── Envelope / delivery metadata (no standard AMQP name → redb-prefixed) ─────

    /// <summary>Exchange the message was published to.</summary>
    public const string Exchange = "redbRmq.Exchange";

    /// <summary>Routing key of the message.</summary>
    public const string RoutingKey = "redbRmq.RoutingKey";

    /// <summary>Delivery tag for acknowledgement.</summary>
    public const string DeliveryTag = "redbRmq.DeliveryTag";

    /// <summary>Whether this message was redelivered.</summary>
    public const string Redelivered = "redbRmq.Redelivered";

    /// <summary>Consumer tag that received the message.</summary>
    public const string ConsumerTag = "redbRmq.ConsumerTag";

    // ── Standard AMQP basic properties (well-known names → BARE, self-documenting) ──
    // These map 1:1 to RabbitMQ BasicProperties. Bare names mean no docs needed and a
    // consume→produce round-trip carries them through. Caveat: common-word names
    // (Type/Priority/Timestamp/MessageId) can collide with a business header of the
    // same name — set a prefixed header instead if that matters.

    /// <summary>AMQP content type (BasicProperties.ContentType).</summary>
    public const string ContentType = "ContentType";

    /// <summary>Correlation ID (BasicProperties.CorrelationId).</summary>
    public const string CorrelationId = "CorrelationId";

    /// <summary>Reply-to address (BasicProperties.ReplyTo).</summary>
    public const string ReplyTo = "ReplyTo";

    /// <summary>Message ID (BasicProperties.MessageId).</summary>
    public const string MessageId = "MessageId";

    /// <summary>Message priority 0-9 (BasicProperties.Priority).</summary>
    public const string Priority = "Priority";

    /// <summary>Per-message expiration / TTL in milliseconds, as a string (BasicProperties.Expiration).</summary>
    public const string Expiration = "Expiration";

    /// <summary>Application message type name (BasicProperties.Type).</summary>
    public const string Type = "Type";

    /// <summary>Creating application id (BasicProperties.AppId).</summary>
    public const string AppId = "AppId";

    /// <summary>Creating user id (BasicProperties.UserId).</summary>
    public const string UserId = "UserId";

    /// <summary>Content encoding (BasicProperties.ContentEncoding).</summary>
    public const string ContentEncoding = "ContentEncoding";

    /// <summary>Message timestamp, Unix seconds (BasicProperties.Timestamp).</summary>
    public const string Timestamp = "Timestamp";

    /// <summary>Returns true if the header key belongs to the RabbitMQ component.</summary>
    public static bool IsRedbHeader(string key) =>
        key.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);
}

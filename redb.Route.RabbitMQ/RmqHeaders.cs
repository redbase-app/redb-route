namespace redb.Route.RabbitMQ;

/// <summary>
/// Well-known header constants used by the RabbitMQ component.
/// </summary>
public static class RmqHeaders
{
    /// <summary>Common prefix for all RabbitMQ component headers.</summary>
    public const string Prefix = "redbRmq.";

    // ── Headers set on the In message by the consumer ────────────────

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

    /// <summary>AMQP content type (if set on BasicProperties).</summary>
    public const string ContentType = "redbRmq.ContentType";

    /// <summary>Correlation ID (if set on BasicProperties).</summary>
    public const string CorrelationId = "redbRmq.CorrelationId";

    /// <summary>Reply-to address (if set on BasicProperties).</summary>
    public const string ReplyTo = "redbRmq.ReplyTo";

    /// <summary>Message ID (if set on BasicProperties).</summary>
    public const string MessageId = "redbRmq.MessageId";

    /// <summary>Returns true if the header key belongs to the RabbitMQ component.</summary>
    public static bool IsRedbHeader(string key) =>
        key.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);
}

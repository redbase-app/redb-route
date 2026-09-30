namespace redb.Route.Core;

/// <summary>
/// When a consumer settles the message it received — acknowledges it to RabbitMQ, accepts it on AMQP, commits the
/// Kafka offset, deletes it from SQS, completes it on Service Bus, XACKs the Redis stream entry. One option,
/// <c>ackMode</c>, with the same meaning on every broker consumer.
/// <para>
/// It is independent of <c>.Transacted()</c>: the transaction owns the database and the outgoing sends; the
/// acknowledgement of the incoming message is always the consumer's, once, after the transaction has committed.
/// </para>
/// </summary>
public enum AckMode
{
    /// <summary>
    /// Settled once the route has run: acknowledged when the exchange ended well (no exception, or one an error
    /// handler handled), rejected or released for redelivery when it failed. At-least-once. The default.
    /// </summary>
    Manual,

    /// <summary>
    /// Settled on receipt, before the route runs — by the broker (RabbitMQ auto-ack, Service Bus receive-and-delete,
    /// Redis NOACK) or by the consumer right away (AMQP accept, Kafka offset, SQS delete). A message whose route then
    /// fails is not delivered again. At-most-once.
    /// </summary>
    Auto,
}

using redb.Route.Abstractions;

namespace redb.Route.RabbitMQ;

/// <summary>
/// The default <see cref="IHeaderFilterStrategy"/> of the RabbitMQ component, Apache Camel's
/// <c>RabbitMQHeaderFilterStrategy</c>. An endpoint's <c>headerFilterStrategy=#name</c> replaces it; derive from it to
/// add to it.
/// <para>
/// A producer's request and a consumer's RPC reply carry every exchange header but the component's own
/// <c>redbRmq.*</c> and null values as AMQP headers; a consumed message and an RPC reply bring every AMQP header in.
/// Whatever the strategy, a header named after a basic property of the message (<c>MessageId</c>, <c>CorrelationId</c>,
/// <c>ReplyTo</c>...) goes into that property, not into the headers, and the delivery metadata
/// (<c>redbRmq.*</c>, the basic properties by name) is set by the consumer.
/// </para>
/// </summary>
public class RabbitMQHeaderFilterStrategy : IHeaderFilterStrategy
{
    /// <summary>The default strategy of a RabbitMQ endpoint.</summary>
    public static RabbitMQHeaderFilterStrategy Default { get; } = new();

    /// <inheritdoc />
    public virtual bool FilterOutgoing(string name, object? value, IExchange exchange)
        => value is null || RmqHeaders.IsRedbHeader(name);

    /// <inheritdoc />
    public virtual bool FilterIncoming(string name, object? value, IExchange exchange) => false;
}

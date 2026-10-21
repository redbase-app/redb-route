using redb.Route.Abstractions;

namespace redb.Route.Kafka;

/// <summary>
/// The default <see cref="IHeaderFilterStrategy"/> of the Kafka component, Apache Camel's
/// <c>KafkaHeaderFilterStrategy</c>. An endpoint's <c>headerFilterStrategy=#name</c> replaces it; derive from it to add
/// to it.
/// <para>
/// A producer sends every exchange header but the component's own <c>redbKafka.*</c>, each as its text; a null value
/// goes as an empty one. A consumer takes every header of a record, as text. Whatever the strategy, a record's
/// <c>content-type</c> header is the exchange's content type (the producer writes it from the exchange, the consumer
/// reads it into the exchange), and the <c>redbKafka.*</c> metadata of a consumed record is set by the consumer.
/// </para>
/// </summary>
public class KafkaHeaderFilterStrategy : IHeaderFilterStrategy
{
    /// <summary>The default strategy of a Kafka endpoint.</summary>
    public static KafkaHeaderFilterStrategy Default { get; } = new();

    /// <inheritdoc />
    public virtual bool FilterOutgoing(string name, object? value, IExchange exchange) => KafkaHeaders.IsRedbHeader(name);

    /// <inheritdoc />
    public virtual bool FilterIncoming(string name, object? value, IExchange exchange) => false;
}

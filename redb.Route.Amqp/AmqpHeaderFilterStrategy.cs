using redb.Route.Abstractions;

namespace redb.Route.Amqp;

/// <summary>
/// The default <see cref="IHeaderFilterStrategy"/> of the AMQP 1.0 component, Apache Camel's
/// <c>AmqpHeaderFilterStrategy</c> counterpart. An endpoint's <c>headerFilterStrategy=#name</c> replaces it; derive
/// from it to add to it.
/// <para>
/// A producer's request and a consumer's reply carry every exchange header but the component's own
/// <c>redbAmqp.*</c> and null values as application-properties; a consumed message and a request-reply's reply bring
/// every application-property in. Whatever the strategy, a producer sends the standard AMQP property names
/// (<c>MessageId</c>, <c>CorrelationId</c>, <c>Subject</c>...) as those properties, not as application-properties,
/// the consumer sets the standard properties and the <c>redbAmqp.*</c> delivery metadata by name, and trace context
/// is written by the telemetry layer.
/// </para>
/// </summary>
public class AmqpHeaderFilterStrategy : IHeaderFilterStrategy
{
    /// <summary>The default strategy of an AMQP endpoint.</summary>
    public static AmqpHeaderFilterStrategy Default { get; } = new();

    /// <inheritdoc />
    public virtual bool FilterOutgoing(string name, object? value, IExchange exchange)
        => value is null || AmqpHeaders.IsRedbHeader(name);

    /// <inheritdoc />
    public virtual bool FilterIncoming(string name, object? value, IExchange exchange) => false;
}

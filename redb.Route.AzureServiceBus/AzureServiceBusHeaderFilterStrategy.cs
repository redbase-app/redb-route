using redb.Route.Abstractions;

namespace redb.Route.AzureServiceBus;

/// <summary>
/// The default <see cref="IHeaderFilterStrategy"/> of the Azure Service Bus component. An endpoint's
/// <c>headerFilterStrategy=#name</c> replaces it; derive from it to add to it.
/// <para>
/// A producer sends every exchange header but the component's own <c>redbAsb.*</c> and null values as application
/// properties; a consumer (plain and session alike) brings every application property in. Whatever the strategy, the
/// producer reads the message's system properties (<c>MessageId</c>, <c>SessionId</c>, <c>Subject</c>...) from the
/// <c>redbAsb.*</c> headers, the consumer sets them and the delivery metadata, and trace context is written by the
/// telemetry layer.
/// </para>
/// </summary>
public class AzureServiceBusHeaderFilterStrategy : IHeaderFilterStrategy
{
    /// <summary>The default strategy of an Azure Service Bus endpoint.</summary>
    public static AzureServiceBusHeaderFilterStrategy Default { get; } = new();

    /// <inheritdoc />
    public virtual bool FilterOutgoing(string name, object? value, IExchange exchange)
        => value is null || AzureServiceBusHeaders.IsAsbHeader(name);

    /// <inheritdoc />
    public virtual bool FilterIncoming(string name, object? value, IExchange exchange) => false;
}

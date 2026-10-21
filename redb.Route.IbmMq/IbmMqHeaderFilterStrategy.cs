using redb.Route.Abstractions;

namespace redb.Route.IbmMq;

/// <summary>
/// The default <see cref="IHeaderFilterStrategy"/> of the IBM MQ component. An endpoint's
/// <c>headerFilterStrategy=#name</c> replaces it; derive from it to add to it.
/// <para>
/// A producer's request and a consumer's reply carry every exchange header but the component's own
/// <c>redbIbmMq.*</c> and null values in the message's header catalogue; a consumed message and a request-reply's
/// reply bring every catalogued header in, on the poll and the XMS receive paths alike. Whatever the strategy, MQMD
/// fields are read from and written to the <c>redbIbmMq.*</c> headers by name, <c>targetClient=Mq</c> sends no
/// catalogue at all, and trace context is written by the telemetry layer.
/// </para>
/// </summary>
public class IbmMqHeaderFilterStrategy : IHeaderFilterStrategy
{
    /// <summary>The default strategy of an IBM MQ endpoint.</summary>
    public static IbmMqHeaderFilterStrategy Default { get; } = new();

    /// <inheritdoc />
    public virtual bool FilterOutgoing(string name, object? value, IExchange exchange)
        => value is null || IbmMqHeaders.IsRedbHeader(name);

    /// <inheritdoc />
    public virtual bool FilterIncoming(string name, object? value, IExchange exchange) => false;
}

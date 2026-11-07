using redb.Route.Abstractions;

namespace redb.Route.Sqs;

/// <summary>
/// The default <see cref="IHeaderFilterStrategy"/> of the SNS component. An endpoint's
/// <c>headerFilterStrategy=#name</c> replaces it; derive from it to add to it.
/// <para>
/// A producer publishes every exchange header but the component's own <c>redbSns.*</c> / <c>redbSqs.*</c> and null
/// values as a message attribute of the same name. Whatever the strategy, a <c>redbSqs.attr.&lt;name&gt;</c> header
/// is an explicit request and goes out as attribute <c>&lt;name&gt;</c>, the other <c>redbSns.*</c> /
/// <c>redbSqs.*</c> headers never go out, and trace context is written by the telemetry layer.
/// </para>
/// </summary>
public class SnsHeaderFilterStrategy : IHeaderFilterStrategy
{
    /// <summary>The default strategy of an SNS endpoint.</summary>
    public static SnsHeaderFilterStrategy Default { get; } = new();

    /// <inheritdoc />
    public virtual bool FilterOutgoing(string name, object? value, IExchange exchange)
        => value is null || SnsHeaders.IsSnsHeader(name) || SqsHeaders.IsSqsHeader(name);

    /// <inheritdoc />
    public virtual bool FilterIncoming(string name, object? value, IExchange exchange) => false;
}

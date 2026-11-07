using redb.Route.Abstractions;

namespace redb.Route.Sqs;

/// <summary>
/// The default <see cref="IHeaderFilterStrategy"/> of the SQS component. An endpoint's
/// <c>headerFilterStrategy=#name</c> replaces it; derive from it to add to it.
/// <para>
/// A producer sends every exchange header but the component's own <c>redbSqs.*</c> / <c>redbSns.*</c> and null values
/// as a message attribute of the same name; a consumer brings every message attribute in. The strategy is asked with
/// the attribute's own name, on the way in as well, although the consumer stores it under
/// <c>redbSqs.attr.&lt;name&gt;</c>.
/// </para>
/// <para>
/// Whatever the strategy: a <c>redbSqs.attr.&lt;name&gt;</c> header is an explicit request and goes out as attribute
/// <c>&lt;name&gt;</c>; the other <c>redbSqs.*</c> / <c>redbSns.*</c> headers never go out; the consumer's
/// <c>redbSqs.*</c> metadata is set by the consumer; trace context is written by the telemetry layer.
/// </para>
/// </summary>
public class SqsHeaderFilterStrategy : IHeaderFilterStrategy
{
    /// <summary>The default strategy of an SQS endpoint.</summary>
    public static SqsHeaderFilterStrategy Default { get; } = new();

    /// <inheritdoc />
    public virtual bool FilterOutgoing(string name, object? value, IExchange exchange)
        => value is null || SqsHeaders.IsSqsHeader(name) || SnsHeaders.IsSnsHeader(name);

    /// <inheritdoc />
    public virtual bool FilterIncoming(string name, object? value, IExchange exchange) => false;
}

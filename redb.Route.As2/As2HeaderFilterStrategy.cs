using redb.Route.Abstractions;

namespace redb.Route.As2;

/// <summary>
/// The default <see cref="IHeaderFilterStrategy"/> of the AS2 component. An endpoint's
/// <c>headerFilterStrategy=#name</c> replaces it; derive from it to add to it.
/// <para>
/// A producer sends every exchange header but the component's own <c>redbAs2.*</c> and null values as an HTTP header
/// of the message; a consumer brings every request header in.
/// </para>
/// <para>
/// Whatever the strategy: the producer never bridges the MIME, AS2 and hop-by-hop fields it sets itself, the
/// credentials of another hop (<c>Authorization</c>, <c>Cookie</c>, <c>Set-Cookie</c>) or <c>Host</c>; the consumer
/// never takes the pseudo-headers, the S/MIME wrapper and hop-by-hop fields or the credentials that authenticated the
/// hop; the consumer's <c>redbAs2.*</c> metadata is set by the consumer.
/// </para>
/// </summary>
public class As2HeaderFilterStrategy : IHeaderFilterStrategy
{
    /// <summary>The default strategy of an AS2 endpoint.</summary>
    public static As2HeaderFilterStrategy Default { get; } = new();

    /// <inheritdoc />
    public virtual bool FilterOutgoing(string name, object? value, IExchange exchange)
        => value is null || As2Headers.IsRedbHeader(name);

    /// <inheritdoc />
    public virtual bool FilterIncoming(string name, object? value, IExchange exchange) => false;
}

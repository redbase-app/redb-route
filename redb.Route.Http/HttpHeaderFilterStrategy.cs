using redb.Route.Abstractions;
using redb.Route.Core;

namespace redb.Route.Http;

/// <summary>
/// The default <see cref="IHeaderFilterStrategy"/> of the HTTP component, Apache Camel's <c>HttpHeaderFilterStrategy</c>.
/// An endpoint's <c>headerFilterStrategy=#name</c> replaces it; derive from it to add to it.
/// <para>
/// A producer (<see cref="Producer"/>) sends every exchange header but the component's control headers
/// (<see cref="HttpHeaders.ControlHeaders"/>) and empty values, and takes every response header back. A consumer
/// (<see cref="Consumer"/>) takes every request header and answers with every exchange header but <c>redb*</c> and
/// <c>Camel*</c> ones and null values.
/// </para>
/// <para>
/// Whatever the strategy, the transport itself never carries the protocol fields (<see cref="HttpHeaders.ProtocolHeaders"/>),
/// and a consumer never takes a received <c>redbHttp.*</c> header (it would pose as one the transport sets, such as
/// the remote address), never takes a response-only field from a request (<see cref="HttpHeaders.ResponseOnlyHeaders"/>)
/// and never answers with a value the request brought.
/// </para>
/// </summary>
public class HttpHeaderFilterStrategy : IHeaderFilterStrategy
{
    /// <summary>The default strategy of an HTTP producer.</summary>
    public static HttpHeaderFilterStrategy Producer { get; } = new(EndpointRole.Producer);

    /// <summary>The default strategy of an HTTP consumer.</summary>
    public static HttpHeaderFilterStrategy Consumer { get; } = new(EndpointRole.Consumer);

    /// <summary>Creates the default strategy of a producer or of a consumer.</summary>
    /// <param name="role">Which side of the exchange the strategy serves.</param>
    public HttpHeaderFilterStrategy(EndpointRole role) => Role = role;

    /// <summary>Which side of the exchange the strategy serves: a producer's request, or a consumer's response.</summary>
    public EndpointRole Role { get; }

    /// <inheritdoc />
    public virtual bool FilterOutgoing(string name, object? value, IExchange exchange)
        => Role == EndpointRole.Producer
            ? value is null || value.ToString() is not { Length: > 0 } || HttpHeaders.ControlHeaders.Contains(name)
            : value is null
              || name.StartsWith("redb", StringComparison.OrdinalIgnoreCase)
              || name.StartsWith("Camel", StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc />
    public virtual bool FilterIncoming(string name, object? value, IExchange exchange) => false;
}

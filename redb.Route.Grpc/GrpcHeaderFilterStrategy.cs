using redb.Route.Abstractions;

namespace redb.Route.Grpc;

/// <summary>
/// The default <see cref="IHeaderFilterStrategy"/> of the gRPC component. An endpoint's
/// <c>headerFilterStrategy=#name</c> replaces it; derive from it to add to it.
/// <para>
/// A producer sends every exchange header but the component's own <c>redbGrpc.*</c> and null values as call metadata
/// and, in envelope mode, as envelope headers; a consumer's reply envelope carries the same. A consumer brings every
/// request metadata entry and envelope header in, and a producer every reply envelope header.
/// </para>
/// <para>
/// Whatever the strategy: HTTP/2 pseudo-headers and <c>grpc-*</c> entries are the protocol's own; a caller's header
/// with a transport-reserved prefix (<c>redbGrpc.</c>, <c>redbHttp.</c>...) is dropped on the way in unless
/// <c>allowClientReservedHeaders=true</c>; a metadata key gRPC cannot express is not sent; the consumer's
/// <c>redbGrpc.*</c> call metadata, the envelope's <c>Content-Type</c> and the <c>redbGrpc.Trailer.*</c> trailers are
/// set by the component; trace context is written by the telemetry layer.
/// </para>
/// </summary>
public class GrpcHeaderFilterStrategy : IHeaderFilterStrategy
{
    /// <summary>The default strategy of a gRPC endpoint.</summary>
    public static GrpcHeaderFilterStrategy Default { get; } = new();

    /// <inheritdoc />
    public virtual bool FilterOutgoing(string name, object? value, IExchange exchange)
        => value is null || GrpcHeaders.IsRedbHeader(name);

    /// <inheritdoc />
    public virtual bool FilterIncoming(string name, object? value, IExchange exchange) => false;
}

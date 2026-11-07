namespace redb.Route.Grpc;

/// <summary>
/// Well-known header constants used by the gRPC component.
/// Exchange headers with the "redbGrpc." prefix are set/read automatically.
/// </summary>
public static class GrpcHeaders
{
    /// <summary>Common prefix for all gRPC component headers.</summary>
    public const string Prefix = "redbGrpc.";

    /// <summary>gRPC service name. Example: "redb.route.grpc.RedbService".</summary>
    public const string Service = "redbGrpc.Service";

    /// <summary>gRPC method name. Example: "Process".</summary>
    public const string Method = "redbGrpc.Method";

    /// <summary>
    /// gRPC status code (int, or the <c>Grpc.Core.StatusCode</c> name). Set by the producer after a
    /// response; set it on the consumer's <c>Out</c> to choose the status the caller receives. When it is
    /// absent the consumer falls back to the transport-neutral <c>status.code</c> that controllers write.
    /// </summary>
    public const string StatusCode = "redbGrpc.StatusCode";

    /// <summary>gRPC status detail message (the <c>grpc-message</c> trailer).</summary>
    public const string StatusDetail = "redbGrpc.StatusDetail";

    /// <summary>
    /// Prefix for response trailers. A header <c>redbGrpc.Trailer.error</c> becomes the trailer
    /// <c>error</c>, so a route can return structured failure detail without disturbing the body.
    /// </summary>
    public const string TrailerPrefix = "redbGrpc.Trailer.";

    /// <summary>Full method address the call arrived on, e.g. <c>/identity.v1.Identity/Token</c>.</summary>
    public const string Route = "redbGrpc.Route";

    /// <summary>Remote peer address (consumer-side), raw gRPC form: <c>ipv4:10.0.0.5:51234</c>.</summary>
    public const string RemotePeer = "redbGrpc.RemotePeer";

    /// <summary>Remote client address, bare (no scheme, no port) — what IP-based policies expect.</summary>
    public const string RemoteIp = "redbGrpc.RemoteIp";

    /// <summary>Remote client port (int).</summary>
    public const string RemotePort = "redbGrpc.RemotePort";

    /// <summary>Thumbprint of the client certificate, when mTLS is enabled and one was presented.</summary>
    public const string ClientCertThumbprint = "redbGrpc.ClientCertThumbprint";

    /// <summary>Subject of the client certificate.</summary>
    public const string ClientCertSubject = "redbGrpc.ClientCertSubject";

    /// <summary>Expiry (ISO 8601) of the client certificate.</summary>
    public const string ClientCertNotAfter = "redbGrpc.ClientCertNotAfter";

    /// <summary>Server port the request was received on (consumer-side).</summary>
    public const string Port = "redbGrpc.Port";

    /// <summary>Authority (host:port) from the gRPC call.</summary>
    public const string Authority = "redbGrpc.Authority";

    /// <summary>Deadline for the gRPC call (ISO 8601), if set by the caller.</summary>
    public const string Deadline = "redbGrpc.Deadline";

    /// <summary>Returns true if the header key belongs to the gRPC component.</summary>
    public static bool IsRedbHeader(string key) =>
        key.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);
}

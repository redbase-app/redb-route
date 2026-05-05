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

    /// <summary>gRPC status code (int). Set by producer after response.</summary>
    public const string StatusCode = "redbGrpc.StatusCode";

    /// <summary>gRPC status detail message.</summary>
    public const string StatusDetail = "redbGrpc.StatusDetail";

    /// <summary>Remote peer address (consumer-side).</summary>
    public const string RemotePeer = "redbGrpc.RemotePeer";

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

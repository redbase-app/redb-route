using redb.Route.Core;

namespace redb.Route.Grpc;

/// <summary>
/// Options for the gRPC endpoint. Shared by both producer and consumer.
/// Use URI parameters to configure: grpc:host:port?deadline=30000
/// </summary>
public class GrpcEndpointOptions : EndpointOptions
{
    // ── Common ──────────────────────────────────────────

    /// <summary>Deadline (timeout) in milliseconds. Default: 30000 (30s). 0 = no deadline.</summary>
    public int Deadline { get; set; } = 30_000;

    /// <summary>Expression override for deadline (resolves to int milliseconds).</summary>
    public string? DeadlineExpression { get; set; }

    // ── Producer (GrpcChannel) ──────────────────────────

    /// <summary>Use plaintext (HTTP/2 without TLS). Default: true. Set to false for TLS.</summary>
    public bool Plaintext { get; set; } = true;

    /// <summary>Max send message size in bytes. Default: 4 MB. 0 = unlimited.</summary>
    public int MaxSendMessageSize { get; set; } = 4 * 1024 * 1024;

    /// <summary>Max receive message size in bytes. Default: 4 MB. 0 = unlimited.</summary>
    public int MaxReceiveMessageSize { get; set; } = 4 * 1024 * 1024;

    // ── Consumer (Kestrel + gRPC server) ────────────────

    /// <summary>Bind host for the embedded gRPC server. Default: 0.0.0.0.</summary>
    public string Host { get; set; } = "0.0.0.0";

    /// <summary>Bind port for the embedded gRPC server. Default: 50051.</summary>
    public int Port { get; set; } = 50051;

    /// <summary>Enable TLS for the consumer. Default: false (plaintext HTTP/2).</summary>
    public bool Ssl { get; set; }

    /// <summary>Path to PFX certificate file for TLS. Required when ssl=true.</summary>
    public string? SslCertPath { get; set; }

    /// <summary>Password for the PFX certificate.</summary>
    public string? SslCertPassword { get; set; }

    /// <summary>Max request message size in bytes for the server. Default: 4 MB. 0 = unlimited.</summary>
    public int MaxRequestMessageSize { get; set; } = 4 * 1024 * 1024;

    /// <summary>
    /// If true, the consumer returns the exchange Out body as the gRPC response (InOut pattern).
    /// If false, returns an empty response (InOnly pattern). Default: true for gRPC.
    /// </summary>
    public bool InOut { get; set; } = true;

    /// <inheritdoc />
    public override void Validate()
    {
        if (Deadline < 0)
            throw new ArgumentException("Deadline must be >= 0.");

        if (Port is < 0 or > 65535)
            throw new ArgumentException("Port must be between 0 and 65535.");

        if (Ssl && string.IsNullOrEmpty(SslCertPath))
            throw new ArgumentException("SslCertPath is required when Ssl=true.");
    }
}

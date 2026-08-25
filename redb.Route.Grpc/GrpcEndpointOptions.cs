using redb.Route.Core;

namespace redb.Route.Grpc;

/// <summary>How the consumer interprets the request body and shapes the reply.</summary>
public enum GrpcEnvelopeMode
{
    /// <summary>
    /// Envelope for the built-in generic method, raw bytes for any other address. Lets the shipped
    /// <c>RedbService</c> keep its headers-plus-payload contract while a typed <c>.proto</c> served on
    /// its own address receives exactly the bytes its client sent.
    /// </summary>
    Auto,

    /// <summary>Always decode the body as a <c>RedbMessage</c> envelope (payload + string headers).</summary>
    Message,

    /// <summary>Never decode: the exchange body is the raw protobuf message the caller sent.</summary>
    Raw,
}

/// <summary>Message compression codec (gRPC <c>grpc-encoding</c>).</summary>
public enum GrpcCompression
{
    /// <summary>Send replies uncompressed. Default. Compressed requests are still accepted.</summary>
    None,

    /// <summary>Gzip replies, but only when the caller advertises gzip in <c>grpc-accept-encoding</c>.</summary>
    Gzip,
}

/// <summary>Whether the consumer requires, allows, or ignores a client certificate (mTLS).</summary>
public enum GrpcClientCertificateMode
{
    /// <summary>No client certificate is requested. Default.</summary>
    NoCertificate,

    /// <summary>A certificate is requested; the call proceeds when the client presents none.</summary>
    AllowCertificate,

    /// <summary>A certificate is required; the handshake fails without one.</summary>
    RequireCertificate,
}

/// <summary>
/// Options for the gRPC endpoint. Shared by both producer and consumer.
/// Use URI parameters to configure: grpc:host:port/service/method?deadline=30000
/// </summary>
public class GrpcEndpointOptions : EndpointOptions
{
    /// <summary>The built-in generic unary method served when the URI carries no method address.</summary>
    public const string DefaultMethodPath = "/redb.route.grpc.RedbService/Process";

    /// <summary>The built-in generic server-streaming method.</summary>
    public const string DefaultStreamMethodPath = "/redb.route.grpc.RedbService/ProcessStream";

    // ── Common ──────────────────────────────────────────

    /// <summary>Deadline (timeout) in milliseconds. Default: 30000 (30s). 0 = no deadline.</summary>
    public int Deadline { get; set; } = 30_000;

    /// <summary>Expression override for deadline (resolves to int milliseconds).</summary>
    public string? DeadlineExpression { get; set; }

    /// <summary>
    /// Full method address this endpoint serves or calls, <c>/package.Service/Method</c>. Taken from the
    /// URI path after <c>host:port</c>; defaults to <see cref="DefaultMethodPath"/>. This is the route
    /// key on the consumer side, so many gRPC methods live on one port as ordinary path routes.
    /// </summary>
    public string MethodPath { get; set; } = DefaultMethodPath;

    /// <summary>Body handling for the consumer. Default: <see cref="GrpcEnvelopeMode.Auto"/>.</summary>
    public GrpcEnvelopeMode Envelope { get; set; } = GrpcEnvelopeMode.Auto;

    /// <summary>
    /// Fully-qualified service name, e.g. <c>identity.v1.Identity</c>. Parity with camel-grpc, which
    /// addresses a service in the URI path and the method as a parameter. Combined with
    /// <see cref="Method"/> it forms <see cref="MethodPath"/>.
    /// </summary>
    public string? Service { get; set; }

    /// <summary>
    /// Method name within <see cref="Service"/>, e.g. <c>Token</c>. Parity with camel-grpc's
    /// <c>method</c> parameter.
    /// </summary>
    public string? Method { get; set; }

    /// <summary>
    /// Single knob for all three size limits (send, receive, server request), mirroring camel-grpc's
    /// <c>maxMessageSize</c>. An explicit per-direction value in the URI wins over it.
    /// </summary>
    public int MaxMessageSize { get; set; }

    /// <summary>
    /// Channel security, mirroring camel-grpc's <c>negotiationType</c>: <c>PLAINTEXT</c> or <c>TLS</c>.
    /// A convenience over <see cref="Plaintext"/> / <see cref="Ssl"/>, which stay authoritative when set
    /// explicitly.
    /// </summary>
    public string? NegotiationType { get; set; }

    // ── Producer (GrpcChannel) ──────────────────────────

    /// <summary>Use plaintext (HTTP/2 without TLS). Default: true. Set to false for TLS.</summary>
    public bool Plaintext { get; set; } = true;

    /// <summary>Max send message size in bytes. Default: 4 MB. 0 = unlimited.</summary>
    public int MaxSendMessageSize { get; set; } = 4 * 1024 * 1024;

    /// <summary>Max receive message size in bytes. Default: 4 MB. 0 = unlimited.</summary>
    public int MaxReceiveMessageSize { get; set; } = 4 * 1024 * 1024;

    /// <summary>
    /// Throw when the call fails, so <c>.OnException(...)</c>, retry and dead-letter see it — the same
    /// contract as the HTTP and SOAP producers. Default: true. Set false for the legacy behaviour where
    /// the failure was only recorded on the exchange and the pipeline carried on.
    /// </summary>
    public bool ThrowOnError { get; set; } = true;

    /// <summary>Path to a PFX client certificate presented to the server (mTLS, producer side).</summary>
    public string? ClientCertPath { get; set; }

    /// <summary>Password for <see cref="ClientCertPath"/>.</summary>
    [Sensitive]
    public string? ClientCertPassword { get; set; }

    // ── Consumer (Kestrel, shared host) ─────────────────

    /// <summary>Bind host for the gRPC server. Default: 0.0.0.0.</summary>
    public string Host { get; set; } = "0.0.0.0";

    /// <summary>Bind port for the gRPC server. Default: 50051.</summary>
    public int Port { get; set; } = 50051;

    /// <summary>Enable TLS for the consumer. Default: false (plaintext HTTP/2, i.e. h2c).</summary>
    public bool Ssl { get; set; }

    /// <summary>Path to PFX certificate file for TLS. Required when ssl=true.</summary>
    public string? SslCertPath { get; set; }

    /// <summary>Password for the PFX certificate.</summary>
    [Sensitive]
    public string? SslCertPassword { get; set; }

    /// <summary>Client-certificate policy (mTLS). Requires <see cref="Ssl"/>.</summary>
    public GrpcClientCertificateMode ClientCertificateMode { get; set; } = GrpcClientCertificateMode.NoCertificate;

    /// <summary>
    /// Comma-separated thumbprints of accepted client certificates. When set, a presented certificate
    /// whose thumbprint is not listed is rejected even if its chain validates.
    /// </summary>
    public string? AllowedClientThumbprints { get; set; }

    /// <summary>
    /// Named <see cref="GrpcConnectionFactory"/> from the route registry. Lets the TLS certificate
    /// password live in the registry instead of the endpoint URI, so it never reaches logs
    /// or dashboards.
    /// </summary>
    public string? ConnectionFactory { get; set; }

    /// <summary>Max request message size in bytes for the server. Default: 4 MB. 0 = unlimited.</summary>
    public int MaxRequestMessageSize { get; set; } = 4 * 1024 * 1024;

    /// <summary>
    /// If true, the consumer returns the exchange Out body as the gRPC response (InOut pattern).
    /// If false, returns an empty response (InOnly pattern). Default: true for gRPC.
    /// </summary>
    public bool InOut { get; set; } = true;

    /// <summary>
    /// Serve this address as a server-streaming method: the route's reply may carry many messages and
    /// each is written as its own frame. Defaults to true for <see cref="DefaultStreamMethodPath"/>.
    /// </summary>
    public bool Streaming { get; set; }

    /// <summary>
    /// Mirror the resolved client address into <c>redbHttp.RemoteAddress</c> as well, so processors
    /// written against the HTTP transport (rate limiting, lockout, device metadata) work unchanged
    /// behind a gRPC facade. Default: false.
    /// </summary>
    public bool EmitHttpCompatHeaders { get; set; }

    /// <summary>
    /// Accept caller-supplied headers that carry a transport-reserved prefix (<c>redbGrpc.</c>,
    /// <c>redbHttp.</c>, …). Default: false — a client must not be able to forge the metadata that
    /// upstream processors trust. Turning this on is logged.
    /// </summary>
    public bool AllowClientReservedHeaders { get; set; }

    /// <summary>
    /// Do not translate a route's <c>status.code</c> into a gRPC status; always answer <c>OK</c> and let
    /// the caller read the error out of the body. Escape hatch for clients written against the old
    /// behaviour. Default: false.
    /// </summary>
    public bool SuppressStatusMapping { get; set; }

    /// <summary>
    /// Also serve <c>grpc.health.v1.Health/Check</c> on this host:port, answering SERVING while the
    /// route is running. Standard probe for Kubernetes, Consul and Envoy. Default: false.
    /// </summary>
    public bool Health { get; set; }

    /// <summary>
    /// Reply compression. Requests are decompressed regardless of this setting (we advertise
    /// <c>identity,gzip</c>); this only decides whether we compress what we send, and even then only when
    /// the caller advertised gzip. Default: <see cref="GrpcCompression.None"/>.
    /// </summary>
    public GrpcCompression Compression { get; set; } = GrpcCompression.None;

    /// <inheritdoc />
    public override void Validate()
    {
        if (Deadline < 0)
            throw new ArgumentException("Deadline must be >= 0.");

        if (Port is < 0 or > 65535)
            throw new ArgumentException("Port must be between 0 and 65535.");

        if (Ssl && string.IsNullOrEmpty(SslCertPath))
            throw new ArgumentException("SslCertPath is required when Ssl=true.");

        if (ClientCertificateMode != GrpcClientCertificateMode.NoCertificate && !Ssl)
            throw new ArgumentException("clientCertificateMode requires ssl=true — mTLS needs a TLS handshake.");

        if (!string.IsNullOrEmpty(MethodPath) && !MethodPath.StartsWith('/'))
            MethodPath = "/" + MethodPath;
    }

    /// <summary>
    /// True when the URI carried no method address, i.e. this endpoint is the built-in generic service.
    /// Such a consumer serves both <c>Process</c> and <c>ProcessStream</c>, as it always has.
    /// </summary>
    internal bool DefaultMethodAddress { get; set; } = true;

    /// <summary>True when the body should be decoded as a <c>RedbMessage</c> envelope.</summary>
    internal bool UseEnvelope => Envelope switch
    {
        GrpcEnvelopeMode.Message => true,
        GrpcEnvelopeMode.Raw => false,
        _ => string.Equals(MethodPath, DefaultMethodPath, StringComparison.OrdinalIgnoreCase)
             || string.Equals(MethodPath, DefaultStreamMethodPath, StringComparison.OrdinalIgnoreCase),
    };
}

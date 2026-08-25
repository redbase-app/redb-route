using System.Text;
using redb.Route.Abstractions;

namespace redb.Route.Grpc;

/// <summary>
/// Fluent entry point for gRPC endpoints.
/// <example><code>
/// // Producer — call a remote gRPC service:
/// .To(GrpcDsl.Call("localhost:50051").Plaintext().Deadline(10_000))
///
/// // Consumer — host a gRPC service:
/// .From(GrpcDsl.Listen("0.0.0.0:50051"))
/// </code></example>
/// </summary>
public static class GrpcDsl
{
    /// <summary>Call a remote gRPC endpoint (producer).</summary>
    /// <param name="hostPort">Target host:port, e.g. <c>localhost:50051</c>.</param>
    public static GrpcBuilder Call(string hostPort) => new(hostPort);

    /// <summary>Listen for incoming gRPC calls (consumer).</summary>
    /// <param name="hostPort">Bind address host:port, e.g. <c>0.0.0.0:50051</c>.</param>
    public static GrpcBuilder Listen(string hostPort) => new(hostPort);
}

/// <summary>Fluent builder for gRPC endpoint URIs. Scheme: <c>grpc</c>.</summary>
public sealed class GrpcBuilder
{
    private readonly string _hostPort;
    private string? _deadline;
    private bool? _plaintext;
    private int? _maxSendMessageSize;
    private int? _maxReceiveMessageSize;
    private int? _maxRequestMessageSize;
    private string? _host;
    private int? _port;
    private bool? _ssl;
    private string? _sslCertPath;
    private string? _sslCertPassword;
    private string? _connectionFactory;
    private bool? _inOut;
    private string? _methodPath;
    private string? _envelope;
    private bool? _streaming;
    private bool? _throwOnError;
    private bool? _health;
    private string? _clientCertificateMode;
    private string? _allowedClientThumbprints;
    private string? _clientCertPath;
    private string? _clientCertPassword;
    private bool? _emitHttpCompatHeaders;
    private bool? _allowClientReservedHeaders;
    private bool? _suppressStatusMapping;
    private string? _service;
    private string? _methodName;
    private int? _maxMessageSize;
    private string? _negotiationType;
    private string? _compression;

    internal GrpcBuilder(string hostPort)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostPort);
        _hostPort = hostPort;
    }

    // ── Common ──────────────────────────────────────────────────────

    /// <summary>Deadline timeout in ms. Default 30000.</summary>
    public GrpcBuilder Deadline(int ms) { _deadline = ms.ToString(); return this; }

    /// <summary>Deadline from an expression (resolves to int milliseconds).</summary>
    public GrpcBuilder Deadline(IExpression expr) { _deadline = expr.ToTemplateString(); return this; }

    /// <summary>
    /// Full method address, <c>/package.Service/Method</c>. On the consumer this is the route key, so
    /// several gRPC methods are served on one port as separate routes; on the producer it is the method
    /// being called. Defaults to the built-in generic <c>RedbService/Process</c>.
    /// </summary>
    public GrpcBuilder Method(string methodPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(methodPath);
        _methodPath = methodPath.StartsWith('/') ? methodPath : "/" + methodPath;
        return this;
    }

    /// <summary>
    /// Body handling. <c>Auto</c> (default) uses the generic <c>RedbMessage</c> envelope only for the
    /// built-in method address; a typed <c>.proto</c> served on its own address gets raw bytes.
    /// </summary>
    public GrpcBuilder Envelope(GrpcEnvelopeMode mode) { _envelope = mode.ToString(); return this; }

    /// <summary>
    /// Service plus method, camel-grpc style (<c>grpc://host:port/my.Service?method=Call</c>). Equivalent
    /// to <see cref="Method(string)"/> with the full address.
    /// </summary>
    public GrpcBuilder Service(string service, string? method = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(service);
        _service = service;
        _methodName = method;
        return this;
    }

    /// <summary>One limit for send, receive and server request sizes (camel-grpc's <c>maxMessageSize</c>).</summary>
    public GrpcBuilder MaxMessageSize(int bytes) { _maxMessageSize = bytes; return this; }

    /// <summary>Reply compression (server) / request compression (client). Requests are always decoded.</summary>
    public GrpcBuilder Compression(GrpcCompression codec) { _compression = codec.ToString(); return this; }

    /// <summary>Channel security, camel-grpc style: <c>PLAINTEXT</c> or <c>TLS</c>.</summary>
    public GrpcBuilder NegotiationType(string type) { _negotiationType = type; return this; }

    // ── Producer ────────────────────────────────────────────────────

    /// <summary>Use plaintext (no TLS). Default true.</summary>
    public GrpcBuilder Plaintext(bool value = true) { _plaintext = value; return this; }

    /// <summary>Max send message size in bytes.</summary>
    public GrpcBuilder MaxSendMessageSize(int bytes) { _maxSendMessageSize = bytes; return this; }

    /// <summary>
    /// Throw when the call fails, so <c>.OnException(...)</c>, retry and dead-letter see it — the same
    /// contract as the HTTP and SOAP producers. Default true.
    /// </summary>
    public GrpcBuilder ThrowOnError(bool value = true) { _throwOnError = value; return this; }

    /// <summary>Client certificate presented to the server (mTLS, producer side).</summary>
    public GrpcBuilder ClientCertificate(string pfxPath, string? password = null)
    {
        _clientCertPath = pfxPath;
        _clientCertPassword = password;
        return this;
    }

    /// <summary>Max receive message size in bytes.</summary>
    public GrpcBuilder MaxReceiveMessageSize(int bytes) { _maxReceiveMessageSize = bytes; return this; }

    // ── Consumer ────────────────────────────────────────────────────

    /// <summary>Bind host for consumer. Default 0.0.0.0.</summary>
    public GrpcBuilder Host(string host) { _host = host; return this; }

    /// <summary>Bind port for consumer.</summary>
    public GrpcBuilder Port(int port) { _port = port; return this; }

    /// <summary>Enable SSL for consumer.</summary>
    public GrpcBuilder Ssl() { _ssl = true; return this; }

    /// <summary>Path to SSL certificate.</summary>
    public GrpcBuilder SslCertPath(string path) { _sslCertPath = path; return this; }

    /// <summary>SSL certificate password.</summary>
    public GrpcBuilder SslCertPassword(string password) { _sslCertPassword = password; return this; }

    /// <summary>References a named <see cref="GrpcConnectionFactory"/> from the route registry instead of putting the certificate password in the URI.</summary>
    public GrpcBuilder ConnectionFactory(string name) { _connectionFactory = name; return this; }

    /// <summary>Max request message size for consumer.</summary>
    public GrpcBuilder MaxRequestMessageSize(int bytes) { _maxRequestMessageSize = bytes; return this; }

    /// <summary>Enable request-response pattern. Default true for gRPC.</summary>
    public GrpcBuilder InOut(bool value = true) { _inOut = value; return this; }

    /// <summary>
    /// Serve this address as a server-streaming method: an <c>IAsyncEnumerable</c> reply body is written
    /// one frame per yield — the framework's own streaming shape, the same one the HTTP consumer honours.
    /// Default true for the built-in <c>RedbService/ProcessStream</c>, opt-in elsewhere.
    /// </summary>
    public GrpcBuilder Streaming(bool value = true) { _streaming = value; return this; }

    /// <summary>Also serve <c>grpc.health.v1.Health/Check</c> on this host:port.</summary>
    public GrpcBuilder Health(bool value = true) { _health = value; return this; }

    /// <summary>
    /// Require or allow a client certificate (mTLS); needs <c>.Ssl()</c>. Optionally pins the accepted
    /// certificates by thumbprint.
    /// </summary>
    public GrpcBuilder ClientCertificates(
        GrpcClientCertificateMode mode = GrpcClientCertificateMode.RequireCertificate,
        params string[] allowedThumbprints)
    {
        _clientCertificateMode = mode.ToString();
        if (allowedThumbprints.Length > 0)
            _allowedClientThumbprints = string.Join(',', allowedThumbprints);
        return this;
    }

    /// <summary>
    /// Mirror the client address into <c>redbHttp.RemoteAddress</c> as well, so processors written
    /// against the HTTP transport (rate limiting, lockout, device metadata) work behind a gRPC facade.
    /// </summary>
    public GrpcBuilder EmitHttpCompatHeaders(bool value = true) { _emitHttpCompatHeaders = value; return this; }

    /// <summary>
    /// Accept caller headers carrying a transport-reserved prefix (<c>redbGrpc.</c>, <c>redbHttp.</c>, …).
    /// Off by default — a client must not be able to forge metadata that upstream processors trust.
    /// </summary>
    public GrpcBuilder AllowClientReservedHeaders(bool value = true) { _allowClientReservedHeaders = value; return this; }

    /// <summary>
    /// Never translate a route's <c>status.code</c> into a gRPC status; always answer OK and let the
    /// caller read the error out of the body. Escape hatch for clients written against the old behaviour.
    /// </summary>
    public GrpcBuilder SuppressStatusMapping(bool value = true) { _suppressStatusMapping = value; return this; }

    // ── Build ───────────────────────────────────────────────────────

    /// <summary>Builds the endpoint URI.</summary>
    public string Build()
    {
        var sb = new StringBuilder();
        sb.Append("grpc:");
        sb.Append(_hostPort);
        // The method address lives in the URI path — it is the route key, not a parameter.
        if (_methodPath is not null && !_hostPort.Contains('/'))
            sb.Append(_methodPath);

        var sep = '?';
        void Append(string key, string v) { sb.Append(sep); sb.Append(key); sb.Append('='); sb.Append(v); sep = '&'; }
        void AppendInt(string key, int? v) { if (v.HasValue) Append(key, v.Value.ToString()); }
        void AppendBool(string key, bool? v) { if (v.HasValue) Append(key, v.Value.ToString().ToLowerInvariant()); }
        void AppendStr(string key, string? v) { if (v != null) Append(key, v); }
        void AppendIntOrExpression(string key, string? v)
        {
            if (v is null) return;
            if (v.Contains("${"))
                Append(key + "Expression", v);
            else
                Append(key, v);
        }

        AppendIntOrExpression("deadline", _deadline);
        AppendBool("plaintext", _plaintext);
        AppendInt("maxSendMessageSize", _maxSendMessageSize);
        AppendInt("maxReceiveMessageSize", _maxReceiveMessageSize);
        AppendStr("host", _host);
        AppendInt("port", _port);
        AppendBool("ssl", _ssl);
        AppendStr("sslCertPath", _sslCertPath);
        AppendStr("sslCertPassword", _sslCertPassword);
        AppendStr("connectionFactory", _connectionFactory);
        AppendInt("maxRequestMessageSize", _maxRequestMessageSize);
        AppendBool("inOut", _inOut);
        AppendStr("envelope", _envelope);
        AppendBool("streaming", _streaming);
        AppendBool("throwOnError", _throwOnError);
        AppendBool("health", _health);
        AppendStr("clientCertificateMode", _clientCertificateMode);
        AppendStr("allowedClientThumbprints", _allowedClientThumbprints);
        AppendStr("clientCertPath", _clientCertPath);
        AppendStr("clientCertPassword", _clientCertPassword);
        AppendBool("emitHttpCompatHeaders", _emitHttpCompatHeaders);
        AppendBool("allowClientReservedHeaders", _allowClientReservedHeaders);
        AppendBool("suppressStatusMapping", _suppressStatusMapping);
        AppendStr("service", _service);
        AppendStr("method", _methodName);
        AppendInt("maxMessageSize", _maxMessageSize);
        AppendStr("negotiationType", _negotiationType);
        AppendStr("compression", _compression);

        return sb.ToString();
    }

    /// <summary>Implicitly converts the builder to its endpoint URI.</summary>
    public static implicit operator string(GrpcBuilder b) => b.Build();

    /// <inheritdoc />
    public override string ToString() => Build();
}

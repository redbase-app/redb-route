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

    // ── Producer ────────────────────────────────────────────────────

    /// <summary>Use plaintext (no TLS). Default true.</summary>
    public GrpcBuilder Plaintext(bool value = true) { _plaintext = value; return this; }

    /// <summary>Max send message size in bytes.</summary>
    public GrpcBuilder MaxSendMessageSize(int bytes) { _maxSendMessageSize = bytes; return this; }

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

    // ── Build ───────────────────────────────────────────────────────

    public string Build()
    {
        var sb = new StringBuilder();
        sb.Append("grpc:");
        sb.Append(_hostPort);

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

        return sb.ToString();
    }

    public static implicit operator string(GrpcBuilder b) => b.Build();
    public override string ToString() => Build();
}

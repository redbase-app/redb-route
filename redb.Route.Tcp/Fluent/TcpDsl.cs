using System.Text;

namespace redb.Route.Tcp;

/// <summary>
/// Fluent entry point for TCP endpoints.
/// <example><code>
/// // Consumer — listen for connections:
/// .From(TcpDsl.Listen("0.0.0.0:9000").TextLine().InOut())
///
/// // Producer — connect to remote host:
/// .To(TcpDsl.Connect("192.168.1.10:9000").TextLine().Reconnect())
/// </code></example>
/// </summary>
public static class TcpDsl
{
    /// <summary>Listen for incoming TCP connections (consumer).</summary>
    public static TcpBuilder Listen(string hostPort) => new(hostPort);

    /// <summary>Connect to a remote TCP endpoint (producer).</summary>
    public static TcpBuilder Connect(string hostPort) => new(hostPort);
}

/// <summary>Fluent builder for TCP endpoint URIs. Scheme: <c>tcp</c>.</summary>
public sealed class TcpBuilder
{
    private readonly string _hostPort;

    // Framing
    private bool _textLine;
    private bool _lengthPrefixed;
    private string? _delimiter;
    private string? _encoding;

    // Socket
    private bool? _keepAlive;
    private bool? _noDelay;
    private int? _receiveBufferSize;
    private int? _sendBufferSize;

    // Producer
    private int? _connectTimeout;
    private bool _reconnect;
    private int? _reconnectInterval;
    private int? _maxReconnectAttempts;

    // Consumer
    private int? _backlog;
    private int? _maxConnections;
    private bool? _inOut;

    // TLS
    private bool _ssl;
    private string? _sslCertPath;
    private string? _sslCertPassword;
    private string? _connectionFactory;
    private string? _sslTargetHost;

    internal TcpBuilder(string hostPort)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostPort);
        _hostPort = hostPort;
    }

    // ── Framing ─────────────────────────────────────────────────────

    /// <summary>Use newline-delimited text framing.</summary>
    public TcpBuilder TextLine() { _textLine = true; return this; }

    /// <summary>Use length-prefixed binary framing.</summary>
    public TcpBuilder LengthPrefixed() { _lengthPrefixed = true; return this; }

    /// <summary>Custom delimiter for text-line framing.</summary>
    public TcpBuilder Delimiter(string delimiter) { _delimiter = delimiter; return this; }

    /// <summary>Character encoding. Default utf-8.</summary>
    public TcpBuilder Encoding(string encoding) { _encoding = encoding; return this; }

    // ── Socket ──────────────────────────────────────────────────────

    /// <summary>Enable TCP keep-alive. Default true.</summary>
    public TcpBuilder KeepAlive(bool value = true) { _keepAlive = value; return this; }

    /// <summary>Set TCP_NODELAY. Default true.</summary>
    public TcpBuilder NoDelay(bool value = true) { _noDelay = value; return this; }

    /// <summary>Receive buffer size in bytes.</summary>
    public TcpBuilder ReceiveBufferSize(int size) { _receiveBufferSize = size; return this; }

    /// <summary>Send buffer size in bytes.</summary>
    public TcpBuilder SendBufferSize(int size) { _sendBufferSize = size; return this; }

    // ── Producer ────────────────────────────────────────────────────

    /// <summary>Connection timeout in ms.</summary>
    public TcpBuilder ConnectTimeout(int ms) { _connectTimeout = ms; return this; }

    /// <summary>Enable auto-reconnect on disconnect.</summary>
    public TcpBuilder Reconnect(int intervalMs = 5000, int maxAttempts = 0) { _reconnect = true; _reconnectInterval = intervalMs; _maxReconnectAttempts = maxAttempts; return this; }

    // ── Consumer ────────────────────────────────────────────────────

    /// <summary>TCP listen backlog. Default 128.</summary>
    public TcpBuilder Backlog(int size) { _backlog = size; return this; }

    /// <summary>Max concurrent connections. 0 = unlimited.</summary>
    public TcpBuilder MaxConnections(int max) { _maxConnections = max; return this; }

    /// <summary>Enable request-response pattern.</summary>
    public TcpBuilder InOut(bool value = true) { _inOut = value; return this; }

    // ── TLS ─────────────────────────────────────────────────────────

    /// <summary>Enable SSL/TLS.</summary>
    public TcpBuilder Ssl() { _ssl = true; return this; }

    /// <summary>Path to SSL certificate.</summary>
    public TcpBuilder SslCertPath(string path) { _sslCertPath = path; return this; }

    /// <summary>SSL certificate password.</summary>
    public TcpBuilder SslCertPassword(string password) { _sslCertPassword = password; return this; }

    /// <summary>References a named <see cref="TcpConnectionFactory"/> from the route registry instead of putting the certificate password in the URI.</summary>
    public TcpBuilder ConnectionFactory(string name) { _connectionFactory = name; return this; }

    /// <summary>Target host name for SSL validation.</summary>
    public TcpBuilder SslTargetHost(string host) { _sslTargetHost = host; return this; }

    // ── Build ───────────────────────────────────────────────────────

    public string Build()
    {
        var sb = new StringBuilder();
        sb.Append("tcp:");
        sb.Append(_hostPort);

        var sep = '?';
        void Append(string key, string v) { sb.Append(sep); sb.Append(key); sb.Append('='); sb.Append(Uri.EscapeDataString(v)); sep = '&'; }
        void AppendInt(string key, int? v) { if (v.HasValue) Append(key, v.Value.ToString()); }
        void AppendBool(string key, bool v) { if (v) Append(key, "true"); }
        void AppendBoolN(string key, bool? v) { if (v.HasValue) Append(key, v.Value.ToString().ToLowerInvariant()); }
        void AppendStr(string key, string? v) { if (v != null) Append(key, v); }

        AppendBool("textLine", _textLine);
        AppendBool("lengthPrefixed", _lengthPrefixed);
        AppendStr("delimiter", _delimiter);
        AppendStr("encoding", _encoding);
        AppendBoolN("keepAlive", _keepAlive);
        AppendBoolN("noDelay", _noDelay);
        AppendInt("receiveBufferSize", _receiveBufferSize);
        AppendInt("sendBufferSize", _sendBufferSize);
        AppendInt("connectTimeout", _connectTimeout);
        AppendBool("reconnect", _reconnect);
        AppendInt("reconnectInterval", _reconnectInterval);
        AppendInt("maxReconnectAttempts", _maxReconnectAttempts);
        AppendInt("backlog", _backlog);
        AppendInt("maxConnections", _maxConnections);
        AppendBoolN("inOut", _inOut);
        AppendBool("ssl", _ssl);
        AppendStr("sslCertPath", _sslCertPath);
        AppendStr("sslCertPassword", _sslCertPassword);
        AppendStr("connectionFactory", _connectionFactory);
        AppendStr("sslTargetHost", _sslTargetHost);

        return sb.ToString();
    }

    public static implicit operator string(TcpBuilder b) => b.Build();
    public override string ToString() => Build();
}

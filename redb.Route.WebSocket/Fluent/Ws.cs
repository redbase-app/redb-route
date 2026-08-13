using System.Text;

namespace redb.Route.WebSocket;

/// <summary>
/// Fluent entry point for WebSocket endpoints.
/// <example><code>
/// // Consumer — listen for WS connections:
/// .From(Ws.Listen("0.0.0.0:8080/chat").SubProtocol("json").InOut())
///
/// // Producer — connect to remote WS:
/// .To(Ws.Connect("api.example.com:443/stream").Ssl().Binary())
///
/// // Secure WebSocket (wss):
/// .To(Ws.Connect("api.example.com/stream").Ssl())
/// </code></example>
/// </summary>
public static class Ws
{
    /// <summary>Listen for WebSocket connections (consumer).</summary>
    /// <param name="hostPortPath">Bind address, e.g. <c>0.0.0.0:8080/chat</c>.</param>
    public static WsBuilder Listen(string hostPortPath) => new(hostPortPath);

    /// <summary>Connect to a remote WebSocket (producer).</summary>
    /// <param name="hostPortPath">Target address, e.g. <c>api.example.com:443/stream</c>.</param>
    public static WsBuilder Connect(string hostPortPath) => new(hostPortPath);
}

/// <summary>Fluent builder for WebSocket endpoint URIs. Scheme: <c>ws</c> (or <c>wss</c> with SSL).</summary>
public sealed class WsBuilder
{
    private readonly string _hostPortPath;

    // Framing
    private string? _messageType;
    private string? _encoding;
    private string? _subProtocol;

    // Socket
    private int? _receiveBufferSize;
    private int? _sendBufferSize;
    private int? _keepAliveInterval;

    // Producer
    private int? _connectTimeout;
    private bool _reconnect;
    private int? _reconnectInterval;
    private int? _maxReconnectAttempts;

    // Consumer
    private int? _maxConnections;
    private bool? _inOut;

    // TLS
    private bool _ssl;
    private string? _sslCertPath;
    private string? _sslCertPassword;

    internal WsBuilder(string hostPortPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostPortPath);
        _hostPortPath = hostPortPath;
    }

    // ── Framing ─────────────────────────────────────────────────────

    /// <summary>Send/receive binary messages instead of text.</summary>
    public WsBuilder Binary() { _messageType = "Binary"; return this; }

    /// <summary>Character encoding. Default utf-8.</summary>
    public WsBuilder Encoding(string encoding) { _encoding = encoding; return this; }

    /// <summary>WebSocket sub-protocol (e.g. <c>graphql-ws</c>, <c>json</c>).</summary>
    public WsBuilder SubProtocol(string protocol) { _subProtocol = protocol; return this; }

    // ── Socket ──────────────────────────────────────────────────────

    /// <summary>Receive buffer size in bytes.</summary>
    public WsBuilder ReceiveBufferSize(int size) { _receiveBufferSize = size; return this; }

    /// <summary>Send buffer size in bytes.</summary>
    public WsBuilder SendBufferSize(int size) { _sendBufferSize = size; return this; }

    /// <summary>Keep-alive interval in ms. Default 30000.</summary>
    public WsBuilder KeepAliveInterval(int ms) { _keepAliveInterval = ms; return this; }

    // ── Producer ────────────────────────────────────────────────────

    /// <summary>Connection timeout in ms.</summary>
    public WsBuilder ConnectTimeout(int ms) { _connectTimeout = ms; return this; }

    /// <summary>Enable auto-reconnect.</summary>
    public WsBuilder Reconnect(int intervalMs = 5000, int maxAttempts = 0) { _reconnect = true; _reconnectInterval = intervalMs; _maxReconnectAttempts = maxAttempts; return this; }

    // ── Consumer ────────────────────────────────────────────────────

    /// <summary>Max concurrent connections. 0 = unlimited.</summary>
    public WsBuilder MaxConnections(int max) { _maxConnections = max; return this; }

    /// <summary>Enable request-response pattern.</summary>
    public WsBuilder InOut(bool value = true) { _inOut = value; return this; }

    // ── TLS ─────────────────────────────────────────────────────────

    /// <summary>Enable SSL/TLS (wss).</summary>
    public WsBuilder Ssl() { _ssl = true; return this; }

    /// <summary>Path to SSL certificate.</summary>
    public WsBuilder SslCertPath(string path) { _sslCertPath = path; return this; }

    /// <summary>SSL certificate password.</summary>
    public WsBuilder SslCertPassword(string password) { _sslCertPassword = password; return this; }

    // ── Build ───────────────────────────────────────────────────────

    public string Build()
    {
        var scheme = _ssl ? "wss" : "ws";
        var sb = new StringBuilder();
        sb.Append(scheme);
        sb.Append(':');
        sb.Append(_hostPortPath);

        var sep = '?';
        void Append(string key, string v) { sb.Append(sep); sb.Append(key); sb.Append('='); sb.Append(Uri.EscapeDataString(v)); sep = '&'; }
        void AppendInt(string key, int? v) { if (v.HasValue) Append(key, v.Value.ToString()); }
        void AppendBool(string key, bool v) { if (v) Append(key, "true"); }
        void AppendBoolN(string key, bool? v) { if (v.HasValue) Append(key, v.Value.ToString().ToLowerInvariant()); }
        void AppendStr(string key, string? v) { if (v != null) Append(key, v); }

        AppendStr("messageType", _messageType);
        AppendStr("encoding", _encoding);
        AppendStr("subProtocol", _subProtocol);
        AppendInt("receiveBufferSize", _receiveBufferSize);
        AppendInt("sendBufferSize", _sendBufferSize);
        AppendInt("keepAliveInterval", _keepAliveInterval);
        AppendInt("connectTimeout", _connectTimeout);
        AppendBool("reconnect", _reconnect);
        AppendInt("reconnectInterval", _reconnectInterval);
        AppendInt("maxReconnectAttempts", _maxReconnectAttempts);
        AppendInt("maxConnections", _maxConnections);
        AppendBoolN("inOut", _inOut);
        AppendBool("ssl", _ssl);
        AppendStr("sslCertPath", _sslCertPath);
        AppendStr("sslCertPassword", _sslCertPassword);

        return sb.ToString();
    }

    public static implicit operator string(WsBuilder b) => b.Build();
    public override string ToString() => Build();
}

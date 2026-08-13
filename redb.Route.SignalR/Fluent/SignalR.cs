using System.Text;
using redb.Route.Abstractions;

namespace redb.Route.SignalR;

/// <summary>
/// Fluent entry point for SignalR endpoints.
/// <example><code>
/// // Consumer — listen for SignalR connections (hub server):
/// .From(SignalR.Hub("0.0.0.0:5000/chatHub").InOut().MessagePack())
///
/// // Producer (client) — connect to a remote SignalR hub:
/// .To(SignalR.Connect("api.example.com:5000/chatHub").Method("Send").Ssl())
///
/// // Producer (server) — broadcast to connected clients via local hub:
/// .To(SignalR.Broadcast("0.0.0.0:5000/chatHub").Method("Notify").Group("room1"))
/// </code></example>
/// </summary>
public static class SignalR
{
    /// <summary>Listen for SignalR connections — start a hub server (consumer).</summary>
    /// <param name="hostPortPath">Bind address, e.g. <c>0.0.0.0:5000/chatHub</c>.</param>
    public static SignalRBuilder Hub(string hostPortPath) => new(hostPortPath);

    /// <summary>Connect to a remote SignalR hub (producer, client mode).</summary>
    /// <param name="hostPortPath">Target address, e.g. <c>api.example.com:5000/chatHub</c>.</param>
    public static SignalRBuilder Connect(string hostPortPath) => new(hostPortPath, SignalRMode.Client);

    /// <summary>Broadcast to clients of a local hub (producer, server mode).</summary>
    /// <param name="hostPortPath">Hub address matching the consumer, e.g. <c>0.0.0.0:5000/chatHub</c>.</param>
    public static SignalRBuilder Broadcast(string hostPortPath) => new(hostPortPath, SignalRMode.Server);
}

/// <summary>Fluent builder for SignalR endpoint URIs. Scheme: <c>signalr</c>.</summary>
public sealed class SignalRBuilder
{
    private readonly string _hostPortPath;
    private SignalRMode? _mode;

    // Common
    private string? _method;
    private bool? _inOut;

    // Transport
    private SignalRTransport? _transport;
    private bool? _messagePack;

    // Groups / targeting
    private string? _defaultGroup;
    private string? _targetType;
    private string? _targetGroup;

    // TLS
    private bool _ssl;
    private string? _sslCertPath;
    private string? _sslCertPassword;

    // Client: reconnect
    private bool _reconnect;
    private int? _reconnectInterval;
    private int? _maxReconnectAttempts;

    // Client: auth
    private string? _accessToken;

    // Client: bridge mode (default true = route through RedbBridgeHub)
    private bool _bridge = true;

    internal SignalRBuilder(string hostPortPath, SignalRMode? mode = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostPortPath);
        _hostPortPath = hostPortPath;
        _mode = mode;
    }

    // ── Common ──────────────────────────────────────────────────────

    /// <summary>Hub method name to invoke/listen on.</summary>
    public SignalRBuilder Method(string method) { _method = method; return this; }

    /// <summary>Hub method name from an expression.</summary>
    public SignalRBuilder Method(IExpression expr) { _method = expr.ToTemplateString(); return this; }

    /// <summary>Enable request-response (InOut) exchange pattern.</summary>
    public SignalRBuilder InOut(bool value = true) { _inOut = value; return this; }

    // ── Transport ───────────────────────────────────────────────────

    /// <summary>Force specific transport type.</summary>
    public SignalRBuilder Transport(SignalRTransport transport) { _transport = transport; return this; }

    /// <summary>Enable MessagePack protocol.</summary>
    public SignalRBuilder MessagePack(bool value = true) { _messagePack = value; return this; }

    // ── Groups / targeting ──────────────────────────────────────────

    /// <summary>Default group for auto-join on connect (consumer).</summary>
    public SignalRBuilder DefaultGroup(string group) { _defaultGroup = group; return this; }

    /// <summary>Target type for server-mode broadcast: all, group, user, connection.</summary>
    public SignalRBuilder Target(string targetType) { _targetType = targetType; return this; }

    /// <summary>Target type from an expression.</summary>
    public SignalRBuilder Target(IExpression expr) { _targetType = expr.ToTemplateString(); return this; }

    /// <summary>Target group name for server-mode broadcast.</summary>
    public SignalRBuilder Group(string group) { _targetType = "group"; _targetGroup = group; return this; }

    /// <summary>Target group name from an expression.</summary>
    public SignalRBuilder Group(IExpression expr) { _targetType = "group"; _targetGroup = expr.ToTemplateString(); return this; }

    // ── TLS ─────────────────────────────────────────────────────────

    /// <summary>Enable SSL/TLS.</summary>
    public SignalRBuilder Ssl() { _ssl = true; return this; }

    /// <summary>Path to SSL certificate.</summary>
    public SignalRBuilder SslCertPath(string path) { _sslCertPath = path; return this; }

    /// <summary>SSL certificate password.</summary>
    public SignalRBuilder SslCertPassword(string password) { _sslCertPassword = password; return this; }

    // ── Client reconnect ────────────────────────────────────────────

    /// <summary>Enable auto-reconnect (client mode).</summary>
    public SignalRBuilder Reconnect(int intervalMs = 5000, int maxAttempts = 0)
    {
        _reconnect = true;
        _reconnectInterval = intervalMs;
        _maxReconnectAttempts = maxAttempts;
        return this;
    }

    /// <summary>Access token for authentication (client mode).</summary>
    public SignalRBuilder AccessToken(string token) { _accessToken = token; return this; }

    /// <summary>
    /// Direct mode — call external hub methods by name instead of routing through the bridge.
    /// Equivalent to <c>bridge=false</c>. Use when connecting to third-party SignalR hubs.
    /// </summary>
    public SignalRBuilder Direct() { _bridge = false; return this; }

    // ── Build ───────────────────────────────────────────────────────

    /// <summary>Builds the endpoint URI string.</summary>
    public string Build()
    {
        var sb = new StringBuilder();
        sb.Append("signalr:");
        sb.Append(_hostPortPath);

        var sep = '?';
        void Append(string key, string v)
        {
            sb.Append(sep);
            sb.Append(key);
            sb.Append('=');
            sb.Append(Uri.EscapeDataString(v));
            sep = '&';
        }

        void AppendStr(string key, string? v) { if (v is not null) Append(key, v); }
        void AppendInt(string key, int? v) { if (v.HasValue) Append(key, v.Value.ToString()); }
        void AppendBool(string key, bool v) { if (v) Append(key, "true"); }
        void AppendBoolN(string key, bool? v) { if (v.HasValue) Append(key, v.Value.ToString().ToLowerInvariant()); }

        if (_mode is not null) Append("mode", _mode.Value.ToString().ToLowerInvariant());
        AppendStr("method", _method);
        AppendBoolN("inOut", _inOut);
        if (_transport is not null) Append("transport", _transport.Value.ToString());
        AppendBoolN("messagePack", _messagePack);
        AppendStr("defaultGroup", _defaultGroup);
        AppendStr("targetType", _targetType);
        AppendStr("targetGroup", _targetGroup);
        AppendBool("ssl", _ssl);
        AppendStr("sslCertPath", _sslCertPath);
        AppendStr("sslCertPassword", _sslCertPassword);
        AppendBool("reconnect", _reconnect);
        AppendInt("reconnectInterval", _reconnectInterval);
        AppendInt("maxReconnectAttempts", _maxReconnectAttempts);
        AppendStr("accessToken", _accessToken);
        if (!_bridge) Append("bridge", "false");

        return sb.ToString();
    }

    /// <summary>Implicit conversion to string URI.</summary>
    public static implicit operator string(SignalRBuilder b) => b.Build();

    /// <inheritdoc />
    public override string ToString() => Build();
}

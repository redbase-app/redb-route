using redb.Route.Core;

namespace redb.Route.WebSocket;

/// <summary>
/// Options for the WebSocket endpoint. Shared by both producer and consumer.
/// URI format: ws:host:port/path?messageType=Text&amp;subProtocol=graphql-ws
/// </summary>
public class WsEndpointOptions : EndpointOptions
{
    // ── Connection ──────────────────────────────────────

    /// <summary>Host to connect or bind. Parsed from URI path.</summary>
    public string Host { get; set; } = "0.0.0.0";

    /// <summary>Port number. Parsed from URI path.</summary>
    public int Port { get; set; } = 8080;

    // ── Framing ─────────────────────────────────────────

    /// <summary>Default message type for outgoing frames. Default: Text.</summary>
    public WsMessageType MessageType { get; set; } = WsMessageType.Text;

    /// <summary>Text encoding name. Default: utf-8.</summary>
    public string Encoding { get; set; } = "utf-8";

    // ── Protocol ────────────────────────────────────────

    /// <summary>
    /// WebSocket subprotocol to negotiate (e.g. "graphql-ws", "mqtt").
    /// Default: null (no subprotocol).
    /// </summary>
    public string? SubProtocol { get; set; }

    // ── Socket ──────────────────────────────────────────

    /// <summary>Receive buffer size in bytes. Default: 8192.</summary>
    public int ReceiveBufferSize { get; set; } = 8192;

    /// <summary>Send buffer size in bytes (ClientWebSocket only). Default: 8192.</summary>
    public int SendBufferSize { get; set; } = 8192;

    /// <summary>Keep-alive ping interval in milliseconds. 0 = disabled. Default: 30000 (30s).</summary>
    public int KeepAliveInterval { get; set; } = 30_000;

    // ── Producer (client) ───────────────────────────────

    /// <summary>Connect timeout in milliseconds. Default: 10000 (10s). 0 = infinite.</summary>
    public int ConnectTimeout { get; set; } = 10_000;

    /// <summary>Auto-reconnect on disconnect. Default: false.</summary>
    public bool Reconnect { get; set; }

    /// <summary>Interval between reconnect attempts in milliseconds. Default: 5000.</summary>
    public int ReconnectInterval { get; set; } = 5_000;

    /// <summary>Maximum reconnect attempts. 0 = unlimited. Default: 0.</summary>
    public int MaxReconnectAttempts { get; set; }

    /// <summary>
    /// Overall time budget for reconnecting, in milliseconds. 0 = no budget (default, the
    /// historical behaviour). With <c>reconnect=true</c>, <c>maxReconnectAttempts=0</c> and a
    /// server that stays down, an exchange would otherwise never come back and dead-letter would
    /// never fire; set this to make the send fail instead of hanging.
    /// </summary>
    public int ReconnectTimeout { get; set; }

    /// <summary>
    /// Producer mode. <c>Client</c> (default) connects to a remote server with
    /// <see cref="System.Net.WebSockets.ClientWebSocket"/>. <c>Server</c> pushes into the clients
    /// of the consumer running on the same URI, so a route can send unsolicited frames
    /// (quotes, notifications, progress) instead of only answering incoming ones.
    /// </summary>
    public WsMode Mode { get; set; } = WsMode.Client;

    // ── Consumer (server) ───────────────────────────────

    /// <summary>Maximum concurrent WebSocket connections. 0 = unlimited. Default: 0.</summary>
    public int MaxConnections { get; set; }

    /// <summary>
    /// If true, the consumer returns the exchange Out body as a WebSocket response frame (InOut pattern).
    /// Default: false.
    /// </summary>
    public bool InOut { get; set; }

    // ── TLS ─────────────────────────────────────────────

    /// <summary>Enable TLS (wss). Default: false.</summary>
    public bool Ssl { get; set; }

    /// <summary>Path to PFX certificate file for TLS (consumer/server). Required when ssl=true for consumer.</summary>
    public string? SslCertPath { get; set; }

    /// <summary>Password for the PFX certificate.</summary>
    [Sensitive]
    public string? SslCertPassword { get; set; }

    /// <summary>
    /// Producer only: accept any server certificate on a <c>wss://</c> connection, including a
    /// self-signed one. Off by default and never implied by another option — turning certificate
    /// validation off has to be an explicit decision, and it belongs to staging, not production.
    /// </summary>
    public bool TrustAllCertificates { get; set; }

    /// <summary>
    /// Named <see cref="WsConnectionFactory"/> from the route registry. Lets the TLS certificate
    /// password live in the registry instead of the endpoint URI, so it never reaches logs
    /// or dashboards.
    /// </summary>
    public string? ConnectionFactory { get; set; }

    /// <inheritdoc />
    public override void Validate()
    {
        if (Port is < 0 or > 65535)
            throw new ArgumentException("Port must be between 0 and 65535.");

        if (ReceiveBufferSize <= 0)
            throw new ArgumentException("ReceiveBufferSize must be > 0.");

        if (SendBufferSize <= 0)
            throw new ArgumentException("SendBufferSize must be > 0.");

        if (KeepAliveInterval < 0)
            throw new ArgumentException("KeepAliveInterval must be >= 0.");

        if (ConnectTimeout < 0)
            throw new ArgumentException("ConnectTimeout must be >= 0.");

        if (ReconnectInterval <= 0)
            throw new ArgumentException("ReconnectInterval must be > 0.");

        if (MaxReconnectAttempts < 0)
            throw new ArgumentException("MaxReconnectAttempts must be >= 0.");

        if (ReconnectTimeout < 0)
            throw new ArgumentException("ReconnectTimeout must be >= 0.");

        if (MaxConnections < 0)
            throw new ArgumentException("MaxConnections must be >= 0.");

        // Without this the mistake surfaces as a bare framework ArgumentException from the
        // consumer's constructor, with no hint which option or which endpoint is at fault.
        try
        {
            System.Text.Encoding.GetEncoding(Encoding);
        }
        catch (ArgumentException ex)
        {
            throw new ArgumentException(
                $"Unknown encoding '{Encoding}'. Use a name System.Text.Encoding knows, such as utf-8.", ex);
        }
    }
}

/// <summary>Producer mode for the WebSocket component.</summary>
public enum WsMode
{
    /// <summary>Connect to a remote WebSocket server and send frames on it.</summary>
    Client,

    /// <summary>Push frames into the clients of the local consumer serving the same URI.</summary>
    Server,
}

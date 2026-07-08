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
    public string? SslCertPassword { get; set; }

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

        if (MaxConnections < 0)
            throw new ArgumentException("MaxConnections must be >= 0.");
    }
}

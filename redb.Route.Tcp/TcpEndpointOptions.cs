using redb.Route.Core;

namespace redb.Route.Tcp;

/// <summary>
/// Options for the TCP endpoint. Shared by both producer and consumer.
/// Use URI parameters to configure: tcp:host:port?textLine=true&amp;delimiter=\n
/// </summary>
public class TcpEndpointOptions : EndpointOptions
{
    // ── Connection ──────────────────────────────────────

    /// <summary>Remote host (producer) or bind host (consumer). Parsed from URI path.</summary>
    public string Host { get; set; } = "0.0.0.0";

    /// <summary>Remote port (producer) or bind port (consumer). Parsed from URI path.</summary>
    public int Port { get; set; }

    // ── Framing ─────────────────────────────────────────

    /// <summary>Message framing mode. Default: Raw.</summary>
    public TcpFraming Framing { get; set; } = TcpFraming.Raw;

    /// <summary>
    /// Shortcut: set to true to use TextLine framing.
    /// Equivalent to Framing=TextLine. Default: false.
    /// </summary>
    public bool TextLine { get; set; }

    /// <summary>
    /// Shortcut: set to true to use LengthPrefixed framing.
    /// Equivalent to Framing=LengthPrefixed. Default: false.
    /// </summary>
    public bool LengthPrefixed { get; set; }

    /// <summary>
    /// Delimiter for TextLine framing. Default: "\n".
    /// Supports common escape sequences: \n, \r\n, \r.
    /// </summary>
    public string Delimiter { get; set; } = "\n";

    /// <summary>Text encoding name for TextLine framing and string body conversion. Default: utf-8.</summary>
    public string Encoding { get; set; } = "utf-8";

    // ── Socket options ──────────────────────────────────

    /// <summary>TCP keep-alive. Default: true.</summary>
    public bool KeepAlive { get; set; } = true;

    /// <summary>Disable Nagle's algorithm for low-latency. Default: true.</summary>
    public bool NoDelay { get; set; } = true;

    /// <summary>Receive buffer size in bytes. Default: 8192.</summary>
    public int ReceiveBufferSize { get; set; } = 8192;

    /// <summary>Send buffer size in bytes. Default: 8192.</summary>
    public int SendBufferSize { get; set; } = 8192;

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

    /// <summary>Listen backlog for the TCP server. Default: 128.</summary>
    public int Backlog { get; set; } = 128;

    /// <summary>Maximum concurrent client connections. 0 = unlimited. Default: 0.</summary>
    public int MaxConnections { get; set; }

    /// <summary>
    /// If true, the consumer returns the exchange Out body as the TCP response (InOut pattern).
    /// If false, processes the message without responding (InOnly). Default: false.
    /// </summary>
    public bool InOut { get; set; }

    // ── TLS ─────────────────────────────────────────────

    /// <summary>Enable TLS encryption. Default: false.</summary>
    public bool Ssl { get; set; }

    /// <summary>Path to PFX certificate file for TLS (consumer). Required when ssl=true for consumer.</summary>
    public string? SslCertPath { get; set; }

    /// <summary>Password for the PFX certificate.</summary>
    public string? SslCertPassword { get; set; }

    /// <summary>
    /// Target host name for TLS server certificate validation (producer).
    /// If not set, uses the Host value.
    /// </summary>
    public string? SslTargetHost { get; set; }

    /// <inheritdoc />
    public override void Validate()
    {
        if (Port is < 0 or > 65535)
            throw new ArgumentException("Port must be between 0 and 65535.");

        if (ReceiveBufferSize <= 0)
            throw new ArgumentException("ReceiveBufferSize must be > 0.");

        if (SendBufferSize <= 0)
            throw new ArgumentException("SendBufferSize must be > 0.");

        if (ConnectTimeout < 0)
            throw new ArgumentException("ConnectTimeout must be >= 0.");

        if (ReconnectInterval <= 0)
            throw new ArgumentException("ReconnectInterval must be > 0.");

        if (MaxReconnectAttempts < 0)
            throw new ArgumentException("MaxReconnectAttempts must be >= 0.");

        if (Backlog <= 0)
            throw new ArgumentException("Backlog must be > 0.");

        if (MaxConnections < 0)
            throw new ArgumentException("MaxConnections must be >= 0.");

        // Resolve framing shortcuts
        if (TextLine) Framing = TcpFraming.TextLine;
        if (LengthPrefixed) Framing = TcpFraming.LengthPrefixed;

        if (TextLine && LengthPrefixed)
            throw new ArgumentException("Cannot enable both TextLine and LengthPrefixed framing.");
    }
}

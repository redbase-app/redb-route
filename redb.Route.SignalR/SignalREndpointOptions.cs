using redb.Route.Core;

namespace redb.Route.SignalR;

/// <summary>
/// Options for the SignalR endpoint. Bound from URI parameters via reflection.
/// URI format: signalr:host:port/hubPath?mode=Client&amp;inOut=true&amp;messagePack=true
/// </summary>
public class SignalREndpointOptions : EndpointOptions
{
    // ── Connection ──────────────────────────────────────

    /// <summary>Bind/connect host. Parsed from URI path. Default: 0.0.0.0.</summary>
    public string Host { get; set; } = "0.0.0.0";

    /// <summary>Bind/connect port. Parsed from URI path. Default: 5000.</summary>
    public int Port { get; set; } = 5000;

    // ── Dispatch ────────────────────────────────────────

    /// <summary>
    /// Producer mode: Client (HubConnection to remote) or Server (IHubContext broadcast).
    /// Ignored for consumer. Default: Client.
    /// </summary>
    public SignalRMode Mode { get; set; } = SignalRMode.Client;

    /// <summary>
    /// Hub method name. For consumer: filter invocations to this method only.
    /// For producer: target method to invoke/send. If null, taken from exchange header.
    /// </summary>
    public string? Method { get; set; }

    // ── Groups ──────────────────────────────────────────

    /// <summary>
    /// Default group name. If set, OnConnectedAsync automatically adds the connection to this group.
    /// </summary>
    public string? DefaultGroup { get; set; }

    // ── Exchange ────────────────────────────────────────

    /// <summary>
    /// If true, the consumer returns exchange Out body as the hub method return value (InOut pattern).
    /// Default: false.
    /// </summary>
    public bool InOut { get; set; }

    // ── Bridge ──────────────────────────────────────────

    /// <summary>
    /// When true (default), client-mode producer calls the bridge hub entry point
    /// <c>Invoke(method, args)</c> — for connecting to our <see cref="RedbBridgeHub"/>.
    /// When false, producer calls the hub method directly by name — for connecting to
    /// external (third-party) SignalR hubs that expose real named methods.
    /// </summary>
    public bool Bridge { get; set; } = true;

    // ── Transport ───────────────────────────────────────

    /// <summary>Preferred transport for client connections. Default: WebSockets.</summary>
    public SignalRTransport Transport { get; set; } = SignalRTransport.WebSockets;

    /// <summary>Use MessagePack protocol instead of JSON. Default: false.</summary>
    public bool MessagePack { get; set; }

    // ── Producer (client mode) ──────────────────────────

    /// <summary>Auto-reconnect on disconnect (client mode). Default: false.</summary>
    public bool Reconnect { get; set; }

    /// <summary>Interval between reconnect attempts in milliseconds (client mode). Default: 5000.</summary>
    public int ReconnectInterval { get; set; } = 5_000;

    /// <summary>Maximum reconnect attempts. 0 = unlimited. Default: 0.</summary>
    public int MaxReconnectAttempts { get; set; }

    // ── Producer (server mode) ──────────────────────────

    /// <summary>
    /// Default target for server-mode producer: All, Group, User, Connection.
    /// Can be overridden per-exchange via signalR.Target header. Default: "All".
    /// </summary>
    public string TargetType { get; set; } = "All";

    /// <summary>Default group for server-mode producer. Overridden by signalR.Group header.</summary>
    public string? TargetGroup { get; set; }

    // ── TLS ─────────────────────────────────────────────

    /// <summary>Enable TLS. Default: false.</summary>
    public bool Ssl { get; set; }

    /// <summary>Path to PFX certificate file for TLS (consumer/server).</summary>
    public string? SslCertPath { get; set; }

    /// <summary>Password for the PFX certificate.</summary>
    [Sensitive]
    public string? SslCertPassword { get; set; }

    /// <summary>
    /// Named <see cref="SignalRConnectionFactory"/> from the route registry. Lets the access token
    /// and TLS certificate password live in the registry instead of the endpoint URI, so they
    /// never reach logs or dashboards.
    /// </summary>
    public string? ConnectionFactory { get; set; }

    // ── Auth ────────────────────────────────────────────

    /// <summary>Access token for client authentication (JWT). Used by producer in client mode.</summary>
    [Sensitive]
    public string? AccessToken { get; set; }

    /// <inheritdoc />
    public override void Validate()
    {
        if (Port is < 0 or > 65535)
            throw new ArgumentException("Port must be between 0 and 65535.");

        if (ReconnectInterval <= 0)
            throw new ArgumentException("ReconnectInterval must be > 0.");

        if (MaxReconnectAttempts < 0)
            throw new ArgumentException("MaxReconnectAttempts must be >= 0.");
    }
}

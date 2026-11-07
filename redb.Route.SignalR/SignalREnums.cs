namespace redb.Route.SignalR;

/// <summary>
/// SignalR producer mode. Determines how the producer sends messages.
/// </summary>
public enum SignalRMode
{
    /// <summary>HubConnection — connects to an external SignalR hub as a client.</summary>
    Client,

    /// <summary>IHubContext — broadcasts to clients of the local hub (requires consumer on the same endpoint).</summary>
    Server
}

/// <summary>
/// SignalR transport type for client connections.
/// </summary>
public enum SignalRTransport
{
    /// <summary>WebSocket transport (default, best performance).</summary>
    WebSockets,

    /// <summary>Server-Sent Events fallback.</summary>
    ServerSentEvents,

    /// <summary>Long polling fallback.</summary>
    LongPolling
}

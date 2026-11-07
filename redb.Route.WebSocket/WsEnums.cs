namespace redb.Route.WebSocket;

/// <summary>
/// WebSocket message type. Maps to <see cref="System.Net.WebSockets.WebSocketMessageType"/>.
/// </summary>
public enum WsMessageType
{
    /// <summary>UTF-8 text frame.</summary>
    Text,

    /// <summary>Binary frame.</summary>
    Binary
}

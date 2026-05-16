namespace redb.Route.WebSocket;

/// <summary>
/// Well-known header constants used by the WebSocket component.
/// </summary>
public static class WsHeaders
{
    /// <summary>Common prefix for all WebSocket component headers.</summary>
    public const string Prefix = "redbWs.";

    /// <summary>Remote endpoint address (IP:port).</summary>
    public const string RemoteAddress = "redbWs.RemoteAddress";

    /// <summary>Local endpoint address (IP:port).</summary>
    public const string LocalAddress = "redbWs.LocalAddress";

    /// <summary>Unique connection identifier.</summary>
    public const string ConnectionId = "redbWs.ConnectionId";

    /// <summary>Message type: "Text" or "Binary".</summary>
    public const string MessageType = "redbWs.MessageType";

    /// <summary>Message byte count.</summary>
    public const string ByteCount = "redbWs.ByteCount";

    /// <summary>WebSocket subprotocol negotiated.</summary>
    public const string SubProtocol = "redbWs.SubProtocol";

    /// <summary>Whether TLS/WSS is used: "True"/"False".</summary>
    public const string Ssl = "redbWs.Ssl";

    /// <summary>Request path the WebSocket was accepted on.</summary>
    public const string Path = "redbWs.Path";
}

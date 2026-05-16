namespace redb.Route.Tcp;

/// <summary>
/// Well-known header keys for TCP exchanges.
/// </summary>
public static class TcpHeaders
{
    /// <summary>Common prefix for all TCP component headers.</summary>
    public const string Prefix = "redbTcp.";

    /// <summary>Remote endpoint address (IP:port) of the connected peer.</summary>
    public const string RemoteAddress = "redbTcp.RemoteAddress";

    /// <summary>Local endpoint address (IP:port) of the local socket.</summary>
    public const string LocalAddress = "redbTcp.LocalAddress";

    /// <summary>Connection ID (unique per accepted client connection on consumer).</summary>
    public const string ConnectionId = "redbTcp.ConnectionId";

    /// <summary>The framing mode used for this exchange.</summary>
    public const string Framing = "redbTcp.Framing";

    /// <summary>Whether TLS is active on this connection.</summary>
    public const string Ssl = "redbTcp.Ssl";

    /// <summary>Number of bytes in the raw message before decoding.</summary>
    public const string ByteCount = "redbTcp.ByteCount";
}

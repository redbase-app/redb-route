namespace redb.Route.SignalR;

/// <summary>
/// Well-known header constants used by the SignalR component.
/// </summary>
public static class SignalRHeaders
{
    /// <summary>Common prefix for all SignalR component headers.</summary>
    public const string Prefix = "redbSignalR.";

    /// <summary>Hub method name invoked by the client or targeted by the producer.</summary>
    public const string Method = "redbSignalR.Method";

    /// <summary>SignalR connection identifier.</summary>
    public const string ConnectionId = "redbSignalR.ConnectionId";

    /// <summary>Authenticated user identifier.</summary>
    public const string UserId = "redbSignalR.UserId";

    /// <summary>Hub lifecycle event: "Connected" or "Disconnected".</summary>
    public const string Event = "redbSignalR.Event";

    /// <summary>Hub path the message was received on.</summary>
    public const string HubPath = "redbSignalR.HubPath";

    /// <summary>Protocol in use: "json" or "messagepack".</summary>
    public const string Protocol = "redbSignalR.Protocol";

    /// <summary>Whether TLS is used: "True"/"False".</summary>
    public const string Ssl = "redbSignalR.Ssl";

    // ── Producer targeting headers ──

    /// <summary>Target audience for producer broadcast: "All", "Group", "User", "Connection".</summary>
    public const string Target = "redbSignalR.Target";

    /// <summary>Target group name for producer broadcast.</summary>
    public const string Group = "redbSignalR.Group";

    /// <summary>Target connection ID for producer send.</summary>
    public const string TargetConnection = "redbSignalR.TargetConnection";

    /// <summary>Target user ID for producer send.</summary>
    public const string TargetUser = "redbSignalR.TargetUser";

    // ── Group management headers (post-process commands) ──

    /// <summary>Add the current connection to the specified group after processing.</summary>
    public const string AddToGroup = "redbSignalR.AddToGroup";

    /// <summary>Remove the current connection from the specified group after processing.</summary>
    public const string RemoveFromGroup = "redbSignalR.RemoveFromGroup";

    /// <summary>Exception message from disconnection (set on Disconnected event).</summary>
    public const string DisconnectError = "redbSignalR.DisconnectError";

    /// <summary>Returns true if the header key starts with the SignalR prefix.</summary>
    public static bool IsRedbHeader(string key) => key.StartsWith(Prefix, StringComparison.Ordinal);
}

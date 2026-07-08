using System.Text.Json.Nodes;
using redb.Route.Llm.Mcp.Protocol;

namespace redb.Route.Llm.Mcp;

/// <summary>
/// Health state of an <see cref="IMcpClient"/>. Surfaced by <see cref="IMcpRegistry"/>
/// so observers can drop dead servers from the agent toolset.
/// </summary>
public enum McpClientStatus
{
    /// <summary>Not yet started.</summary>
    Idle,

    /// <summary>Currently performing the <c>initialize</c> + <c>tools/list</c> handshake.</summary>
    Connecting,

    /// <summary>Connected and ready to handle <c>tools/call</c>.</summary>
    Healthy,

    /// <summary>Lost connection — auto-restart in flight.</summary>
    Restarting,

    /// <summary>Restart budget exhausted — stays dead until a manual restart.</summary>
    Dead
}

/// <summary>
/// Transport-agnostic MCP client. Implementations spawn / hold the connection,
/// perform the protocol handshake, and dispatch <c>tools/call</c> requests
/// against a single MCP server. Lifetime is owned by the host (singleton in DI).
/// </summary>
public interface IMcpClient : IAsyncDisposable
{
    /// <summary>Logical server name as registered with <see cref="IMcpRegistry"/>.</summary>
    string ServerName { get; }

    /// <summary>Current health state of the client.</summary>
    McpClientStatus Status { get; }

    /// <summary>Server identification reported during <c>initialize</c>; null until handshake completes.</summary>
    InitializeResult? Initialize { get; }

    /// <summary>
    /// Raised after a <c>notifications/tools/list_changed</c> from the server,
    /// or after the client reconnects (so the registry refreshes the descriptor set).
    /// </summary>
    event EventHandler? ToolsChanged;

    /// <summary>Performs the <c>initialize</c> handshake. Must be called before any other method.</summary>
    Task InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>Returns the current tool catalogue via <c>tools/list</c>.</summary>
    Task<IReadOnlyList<ToolDefinition>> ListToolsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Invokes a tool via <c>tools/call</c>. On <paramref name="cancellationToken"/> trip the client
    /// sends a <c>notifications/cancelled</c> for the in-flight id and removes the pending TCS.
    /// </summary>
    /// <param name="toolName">Server-side tool name.</param>
    /// <param name="arguments">Arguments matching the tool's <c>inputSchema</c>; null = empty object.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<CallToolResult> CallToolAsync(string toolName, JsonNode? arguments, CancellationToken cancellationToken = default);
}

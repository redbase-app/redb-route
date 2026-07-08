using redb.Route.Core;

namespace redb.Route.Llm.Mcp;

/// <summary>
/// Endpoint options for the <c>mcp://</c> scheme. The URI parser maps
/// <c>mcp://serverName/toolName</c> to <see cref="ServerName"/> + <see cref="ToolName"/>.
/// Optional <c>?callTimeout=PT30S</c> overrides the per-call timeout.
/// </summary>
public sealed class McpEndpointOptions : EndpointOptions
{
    /// <summary>Logical MCP server name (registry key set by <c>AddMcpServer</c>).</summary>
    public string ServerName { get; set; } = "";

    /// <summary>Server-side tool name (raw name as returned by <c>tools/list</c>).</summary>
    public string ToolName { get; set; } = "";

    /// <summary>Per-call timeout in milliseconds. Default 60s. 0 = no client-side timeout.</summary>
    public int CallTimeoutMs { get; set; } = 60_000;

    /// <inheritdoc />
    public override void Validate()
    {
        if (string.IsNullOrWhiteSpace(ServerName))
            throw new InvalidOperationException("mcp:// URI requires a server name (mcp://serverName/toolName).");
        if (string.IsNullOrWhiteSpace(ToolName))
            throw new InvalidOperationException("mcp:// URI requires a tool name (mcp://serverName/toolName).");
        if (CallTimeoutMs < 0)
            throw new ArgumentOutOfRangeException(nameof(CallTimeoutMs), "CallTimeoutMs must be non-negative.");
    }
}

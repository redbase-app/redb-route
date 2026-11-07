namespace redb.Route.Llm.Mcp;

/// <summary>
/// Thrown when an MCP server returns a JSON-RPC error, the transport fails,
/// or the protocol is violated. Catch at the MCP boundary; do not surface raw
/// JSON-RPC errors to the agent.
/// </summary>
public class McpException : Exception
{
    /// <summary>Creates a new <see cref="McpException"/> with a message.</summary>
    public McpException(string message) : base(message) { }

    /// <summary>Creates a new <see cref="McpException"/> with a message and inner exception.</summary>
    public McpException(string message, Exception inner) : base(message, inner) { }
}

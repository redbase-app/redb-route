using redb.Route.Abstractions;
using redb.Route.Core;

namespace redb.Route.Llm.Mcp;

/// <summary>
/// MCP endpoint — a single (server, tool) coordinate. Producer-only.
/// </summary>
public sealed class McpEndpoint : EndpointBase<McpEndpointOptions>
{
    /// <summary>Creates a new MCP endpoint.</summary>
    /// <param name="uri">Parsed URI.</param>
    /// <param name="component">Owning component.</param>
    /// <param name="options">Endpoint options.</param>
    public McpEndpoint(EndpointUri uri, McpComponent component, McpEndpointOptions options)
        : base(uri, component, options)
    {
    }

    /// <summary>The MCP server registry, resolved from the parent component.</summary>
    internal IMcpRegistry Registry => ((McpComponent)Component).Registry;

    /// <summary>Logical server name from the URI path.</summary>
    public string ServerName => Options.ServerName;

    /// <summary>Server-side tool name from the URI path.</summary>
    public string ToolName => Options.ToolName;

    /// <inheritdoc />
    public override IProducer CreateProducer() => new McpProducer(this, Options);

    /// <inheritdoc />
    public override IConsumer CreateConsumer(IProcessor processor)
        => throw new NotSupportedException(
            "mcp:// is producer-only. MCP servers are invoked via tools/call from the agent loop.");
}

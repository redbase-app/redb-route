using redb.Route.Abstractions;
using redb.Route.Core;

namespace redb.Route.Llm.Mcp;

/// <summary>
/// MCP component. Scheme: <c>mcp</c>. Endpoint URI: <c>mcp://serverName/toolName</c>.
/// Producer-only — invokes <c>tools/call</c> on the named MCP server.
/// </summary>
public sealed class McpComponent : ComponentBase
{
    private readonly IMcpRegistry _registry;

    /// <summary>Creates the component over the given MCP client registry.</summary>
    /// <param name="registry">Registry of MCP clients keyed by server name.</param>
    public McpComponent(IMcpRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        _registry = registry;
    }

    /// <inheritdoc />
    public override string Scheme => "mcp";

    /// <summary>Resolved registry — exposed to <see cref="McpEndpoint"/>.</summary>
    internal IMcpRegistry Registry => _registry;

    /// <inheritdoc />
    public override IEndpoint CreateEndpoint(EndpointUri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);

        var (server, tool) = SplitPath(uri.Path);

        var options = new McpEndpointOptions { ServerName = server, ToolName = tool };
        options.BindFromUri(uri.RawParameters);
        // Path values win over query params for the canonical fields.
        options.ServerName = server;
        options.ToolName = tool;
        options.Validate();

        return new McpEndpoint(uri, this, options);
    }

    private static (string Server, string Tool) SplitPath(string path)
    {
        if (string.IsNullOrEmpty(path))
            throw new ArgumentException("mcp:// URI requires a path of the form 'serverName/toolName'.", nameof(path));

        var slash = path.IndexOf('/');
        if (slash <= 0 || slash == path.Length - 1)
            throw new ArgumentException(
                $"Invalid mcp:// URI path '{path}'. Expected 'serverName/toolName'.", nameof(path));

        return (path[..slash], path[(slash + 1)..]);
    }
}

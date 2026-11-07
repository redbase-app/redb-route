using redb.Route.Abstractions;
using redb.Route.Core;

namespace redb.Route.Llm.Mcp;

/// <summary>
/// MCP component. Scheme: <c>mcp</c>. Endpoint URI: <c>mcp://serverName/toolName</c>.
/// Producer-only — invokes <c>tools/call</c> on the named MCP server.
/// </summary>
[EndpointRole(EndpointRole.Producer, Hint = "MCP servers are invoked through tools/call from the agent loop.")]
public sealed class McpComponent : ComponentBase
{
    private IMcpRegistry? _registry;

    /// <summary>
    /// Creates the component over the MCP client registry its context holds, looked up when the first endpoint is
    /// created: the one module code gave the context (<c>ctx.AddService(typeof(IMcpRegistry), registry)</c>, as a Tsak
    /// module does in <c>InitRoute</c>), else the host's <c>AddRedbRouteMcp()</c> singleton. This is the constructor a
    /// package's <c>&lt;component type="…McpComponent"/&gt;</c> and the catalog use.
    /// </summary>
    public McpComponent()
    {
    }

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
    internal IMcpRegistry Registry => _registry ??= ResolveRegistry();

    /// <inheritdoc />
    public override IEndpoint CreateEndpoint(EndpointUri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        _ = Registry; // a context without a registry fails here, when the route is built, not on the first call

        var (server, tool) = SplitPath(uri.Path);

        var options = new McpEndpointOptions { ServerName = server, ToolName = tool };
        options.BindFromUri(uri.RawParameters);
        // Path values win over query params for the canonical fields.
        options.ServerName = server;
        options.ToolName = tool;
        options.Validate();

        return new McpEndpoint(uri, this, options);
    }

    /// <summary>The registry the context holds: the one module code added, else the host's DI singleton.</summary>
    private IMcpRegistry ResolveRegistry()
        => Context?.GetService<IMcpRegistry>()
           ?? Context?.GetServiceProvider()?.GetService(typeof(IMcpRegistry)) as IMcpRegistry
           ?? throw new InvalidOperationException(
               "mcp:// needs the registry of the MCP servers it calls (IMcpRegistry). In a hosted app call " +
               "services.AddRedbRouteMcp() and services.AddMcpServer(name, transport). In a Tsak module build an " +
               "McpRegistry in InitRoute from the module's configuration, initialize and register its clients, and " +
               "give it to the context with ctx.AddService(typeof(IMcpRegistry), registry).");

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

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using redb.Route.Llm.Abstractions.Tools;
using redb.Route.Llm.Mcp.Protocol;
using redb.Route.Llm.Mcp.Transport;

namespace redb.Route.Llm.Mcp;

/// <summary>
/// Hosted service that brings up registered MCP servers, runs the
/// <c>initialize</c> + <c>tools/list</c> handshake, and projects the discovered
/// tools into <see cref="IToolDescriptorRegistry"/>. Listens for
/// <c>notifications/tools/list_changed</c> and rebuilds descriptors when tools
/// mutate. For stdio transports, supervises the child process and restarts it
/// per <see cref="McpRestartPolicy"/>.
/// </summary>
public sealed class McpDiscoveryService : IHostedService, IAsyncDisposable
{
    private readonly IReadOnlyList<McpServerOptions> _servers;
    private readonly IMcpRegistry _registry;
    private readonly IToolDescriptorRegistry _toolRegistry;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<McpDiscoveryService> _logger;

    private readonly Dictionary<string, List<string>> _registeredToolNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _restartAttempts = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    /// <summary>DI-friendly ctor.</summary>
    /// <param name="servers">All <see cref="McpServerOptions"/> registered via <see cref="McpServiceCollectionExtensions.AddMcpServer"/>.</param>
    /// <param name="registry">MCP client registry.</param>
    /// <param name="toolRegistry">LLM tool descriptor registry.</param>
    /// <param name="serviceProvider">DI container — used to resolve a shared <see cref="HttpClient"/> for HTTP transports if registered.</param>
    /// <param name="loggerFactory">Factory for per-server loggers.</param>
    /// <param name="logger">Logger for the discovery service itself.</param>
    public McpDiscoveryService(
        IEnumerable<McpServerOptions> servers,
        IMcpRegistry registry,
        IToolDescriptorRegistry toolRegistry,
        IServiceProvider serviceProvider,
        ILoggerFactory loggerFactory,
        ILogger<McpDiscoveryService> logger)
    {
        _servers = [.. servers];
        _registry = registry;
        _toolRegistry = toolRegistry;
        _serviceProvider = serviceProvider;
        _loggerFactory = loggerFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_servers.Count == 0)
        {
            _logger.LogInformation("MCP discovery: no servers configured.");
            return;
        }

        _logger.LogInformation("MCP discovery: bringing up {Count} server(s).", _servers.Count);

        foreach (var server in _servers)
        {
            try
            {
                await BringUpAsync(server, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "MCP discovery for '{Server}' failed. Other servers continue. Restart will be attempted on next failure.",
                    server.Name);
            }
        }
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("MCP discovery: shutting down {Count} client(s).", _registry.All().Count);
        foreach (var client in _registry.All())
        {
            try { await client.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogDebug(ex, "MCP {Server} dispose threw.", client.ServerName); }
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() => await StopAsync(CancellationToken.None).ConfigureAwait(false);

    // ── internals ───────────────────────────────────────────────────────────

    private async Task BringUpAsync(McpServerOptions server, CancellationToken cancellationToken)
    {
        var client = CreateClient(server);
        client.ToolsChanged += (_, _) => OnToolsChanged(server, client);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(server.DiscoveryTimeout);

        try
        {
            await client.InitializeAsync(timeoutCts.Token).ConfigureAwait(false);
            var tools = await client.ListToolsAsync(timeoutCts.Token).ConfigureAwait(false);

            _registry.Register(client);
            RegisterDescriptors(server, tools);

            _logger.LogInformation(
                "MCP server '{Server}' is healthy with {Count} tool(s).",
                server.Name, tools.Count);
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private IMcpClient CreateClient(McpServerOptions server)
    {
        var logger = _loggerFactory.CreateLogger($"redb.Route.Llm.Mcp.{server.Name}");

        return server.Transport.Kind switch
        {
            McpTransportKind.Stdio => new StdioMcpClient(server.Name, server.Transport, logger),
            McpTransportKind.HttpSse => new HttpSseMcpClient(
                server.Name, server.Transport, logger,
                _serviceProvider.GetService<HttpClient>()),
            _ => throw new InvalidOperationException($"Unknown MCP transport kind: {server.Transport.Kind}"),
        };
    }

    private void RegisterDescriptors(McpServerOptions server, IReadOnlyList<ToolDefinition> tools)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var registeredNames = new List<string>();

        foreach (var tool in tools)
        {
            if (string.IsNullOrWhiteSpace(tool.Name)) continue;

            var modelName = McpToolDescriptor.BuildModelFacingName(server.Name, tool.Name);
            if (!seen.Add(modelName))
            {
                _logger.LogWarning(
                    "MCP {Server} tool '{Tool}' produces duplicate model-facing name '{Model}'. Skipping.",
                    server.Name, tool.Name, modelName);
                continue;
            }

            var safety = ResolveSafety(server, tool.Name);
            var capability = new LlmToolCapability
            {
                Name = modelName,
                Description = tool.Description ?? $"MCP tool '{tool.Name}' from server '{server.Name}'.",
                InputSchema = McpToolDescriptor.BuildInputSchema(tool),
                Safety = safety,
            };

            var descriptor = new McpToolDescriptor(server.Name, tool.Name, capability);
            _toolRegistry.Register(descriptor);
            registeredNames.Add(modelName);
        }

        lock (_gate)
        {
            _registeredToolNames[server.Name] = registeredNames;
        }
    }

    private static LlmToolSafety ResolveSafety(McpServerOptions server, string rawToolName)
    {
        foreach (var ovr in server.SafetyOverrides)
        {
            if (ovr.Matches(rawToolName))
                return ovr.Safety;
        }
        return server.DefaultSafety;
    }

    private void OnToolsChanged(McpServerOptions server, IMcpClient client)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var tools = await client.ListToolsAsync().ConfigureAwait(false);
                RegisterDescriptors(server, tools);
                _logger.LogInformation(
                    "MCP {Server} tools refreshed ({Count} tool(s)).",
                    server.Name, tools.Count);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "MCP {Server} tools refresh failed.", server.Name);
            }
        });
    }
}

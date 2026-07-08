using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using redb.Route.Abstractions;
using redb.Route.Llm.Abstractions.Tools;
using redb.Route.Llm.Mcp.Transport;

namespace redb.Route.Llm.Mcp;

/// <summary>
/// DI registration helpers for <c>redb.Route.Llm.Mcp</c>.
/// </summary>
public static class McpServiceCollectionExtensions
{
    /// <summary>
    /// Registers the MCP component (<c>mcp://</c> scheme), the client registry,
    /// and the discovery hosted service. Call <see cref="AddMcpServer"/> for each
    /// MCP server you want spawned at host startup.
    /// </summary>
    /// <example>
    /// <code>
    /// services.AddRedbRoute(...);
    /// services.AddRedbRouteLlm(...);
    /// services.AddRedbRouteMcp();
    /// services.AddMcpServer("serena", McpTransport.Stdio("uvx",
    ///     ["--from", "git+https://github.com/oraios/serena", "serena", "start-mcp-server",
    ///      "--context", "ide", "--project", "C:/path/to/project"]));
    /// </code>
    /// </example>
    public static IServiceCollection AddRedbRouteMcp(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IMcpRegistry, McpRegistry>();
        services.AddSingleton<McpComponent>();

        services.AddSingleton<IMcpComponentRegistrar>(sp =>
        {
            var context = sp.GetRequiredService<IRouteContext>();
            context.AddComponent(sp.GetRequiredService<McpComponent>());
            return new McpComponentRegistrar();
        });

        // Tool descriptor registry is owned by redb.Route.Llm; don't add a duplicate here.
        // If the consumer hasn't called AddRedbRouteLlm() the discovery service will
        // fail-fast at construction time which is the right diagnostic.
        services.AddHostedService<McpDiscoveryService>();

        return services;
    }

    /// <summary>
    /// Registers a single MCP server entry. The discovery hosted service spawns
    /// the server at host startup, runs <c>initialize</c> + <c>tools/list</c>, and
    /// projects the discovered tools into <see cref="IToolDescriptorRegistry"/>.
    /// </summary>
    /// <param name="services">DI container.</param>
    /// <param name="name">Logical server name — must be unique and matches the registry key.</param>
    /// <param name="transport">Transport configuration (use <see cref="McpTransport.Stdio"/> or <see cref="McpTransport.Http"/>).</param>
    /// <param name="configure">Optional callback to set <c>DiscoveryTimeout</c>, <c>RestartPolicy</c>, or <c>SafetyOverrides</c>.</param>
    public static IServiceCollection AddMcpServer(
        this IServiceCollection services,
        string name,
        McpTransport transport,
        Action<McpServerOptionsBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(transport);

        var builder = new McpServerOptionsBuilder { Name = name, Transport = transport };
        configure?.Invoke(builder);
        var options = builder.Build();

        services.AddSingleton(options);
        return services;
    }
}

/// <summary>Mutable builder for <see cref="McpServerOptions"/>.</summary>
public sealed class McpServerOptionsBuilder
{
    /// <summary>Logical server name.</summary>
    public required string Name { get; set; }

    /// <summary>Transport configuration.</summary>
    public required McpTransport Transport { get; set; }

    /// <summary>Discovery timeout (default 30s).</summary>
    public TimeSpan DiscoveryTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Restart policy (default 3 attempts at 1/3/10s).</summary>
    public McpRestartPolicy RestartPolicy { get; set; } = McpRestartPolicy.Default;

    /// <summary>Per-tool safety overrides.</summary>
    public List<McpSafetyOverride> SafetyOverrides { get; set; } = [];

    /// <summary>Default safety attached when no override matches.</summary>
    public LlmToolSafety DefaultSafety { get; set; } = new()
    {
        SideEffect = ToolSideEffect.External,
        Cost = ToolCostClass.Cheap,
        RequiresApproval = false,
    };

    internal McpServerOptions Build() => new()
    {
        Name = Name,
        Transport = Transport,
        DiscoveryTimeout = DiscoveryTimeout,
        RestartPolicy = RestartPolicy,
        SafetyOverrides = SafetyOverrides,
        DefaultSafety = DefaultSafety,
    };
}

/// <summary>Marker interface for DI registration.</summary>
internal interface IMcpComponentRegistrar;

/// <summary>Marker registration for DI.</summary>
internal sealed class McpComponentRegistrar : IMcpComponentRegistrar;

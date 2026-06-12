using System.Text.RegularExpressions;
using redb.Route.Llm.Abstractions.Tools;
using redb.Route.Llm.Mcp.Transport;

namespace redb.Route.Llm.Mcp;

/// <summary>
/// Restart policy for an MCP stdio server.
/// </summary>
public sealed class McpRestartPolicy
{
    /// <summary>How many auto-restart attempts before giving up. Default 3.</summary>
    public int MaxAttempts { get; init; } = 3;

    /// <summary>Backoff schedule applied between restarts. Default: 1s, 3s, 10s.</summary>
    public IReadOnlyList<TimeSpan> Backoff { get; init; } =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(3),
        TimeSpan.FromSeconds(10),
    ];

    /// <summary>Default policy: 3 attempts at 1s, 3s, 10s.</summary>
    public static McpRestartPolicy Default { get; } = new();

    /// <summary>Disable auto-restart entirely.</summary>
    public static McpRestartPolicy None { get; } = new() { MaxAttempts = 0, Backoff = [] };
}

/// <summary>
/// Per-tool safety override. The first matching <see cref="ToolNamePattern"/> wins.
/// </summary>
public sealed class McpSafetyOverride
{
    /// <summary>Regex matched against the raw server-side tool name.</summary>
    public required string ToolNamePattern { get; init; }

    /// <summary>Replacement <see cref="LlmToolSafety"/> to attach to the descriptor.</summary>
    public required LlmToolSafety Safety { get; init; }

    private Regex? _compiled;
    internal bool Matches(string toolName) => (_compiled ??= new Regex(ToolNamePattern, RegexOptions.Compiled)).IsMatch(toolName);
}

/// <summary>
/// Configuration for a single MCP server entry. Built via
/// <see cref="McpServiceCollectionExtensions.AddMcpServer"/>.
/// </summary>
public sealed class McpServerOptions
{
    /// <summary>Logical server name — used as the registry key and embedded into descriptor names.</summary>
    public required string Name { get; init; }

    /// <summary>Transport configuration (stdio or HTTP+SSE).</summary>
    public required McpTransport Transport { get; init; }

    /// <summary>Maximum time to wait for <c>initialize</c> + <c>tools/list</c>. Default 30s.</summary>
    public TimeSpan DiscoveryTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Auto-restart policy for stdio transports.</summary>
    public McpRestartPolicy RestartPolicy { get; init; } = McpRestartPolicy.Default;

    /// <summary>Per-tool safety overrides (first match wins).</summary>
    public IReadOnlyList<McpSafetyOverride> SafetyOverrides { get; init; } = [];

    /// <summary>Default safety attached when no override matches.</summary>
    public LlmToolSafety DefaultSafety { get; init; } = new()
    {
        SideEffect = ToolSideEffect.External,
        Cost = ToolCostClass.Cheap,
        RequiresApproval = false,
    };
}

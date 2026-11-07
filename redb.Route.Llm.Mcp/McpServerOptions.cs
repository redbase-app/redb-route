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
    private string _toolNamePattern = string.Empty;
    private Regex? _compiled;

    /// <summary>
    /// Regex matched against the <b>raw</b> server-side tool name — the name the MCP server reports,
    /// not the model-facing <c>{server}__{tool}</c> form the descriptor exposes
    /// (<see cref="McpToolDescriptor.BuildModelFacingName"/> sanitises the server name to 24 chars and
    /// the tool name to 36, replacing invalid characters with <c>_</c>). A pattern copied from the
    /// model-facing name therefore matches nothing; the discovery service warns about overrides that
    /// matched no discovered tool.
    /// <para>Example: for server <c>serena</c> and tool <c>find_symbol</c> the pattern is
    /// <c>^find_symbol$</c>, not <c>^serena__find_symbol$</c>.</para>
    /// <para>The pattern is compiled when the override is built, so a typo fails at configuration time
    /// instead of surfacing later as "the server is silently missing from the tool set".</para>
    /// </summary>
    public required string ToolNamePattern
    {
        get => _toolNamePattern;
        init
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(value);
            try
            {
                _compiled = new Regex(value, RegexOptions.Compiled);
            }
            catch (ArgumentException ex)
            {
                throw new ArgumentException(
                    $"MCP safety override pattern '{value}' is not a valid regex: {ex.Message}",
                    nameof(ToolNamePattern), ex);
            }
            _toolNamePattern = value;
        }
    }

    /// <summary>Replacement <see cref="LlmToolSafety"/> to attach to the descriptor.</summary>
    public required LlmToolSafety Safety { get; init; }

    /// <summary>Whether this override accepts <paramref name="toolName"/> (a raw server-side name).</summary>
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

    /// <summary>
    /// Default safety attached when no override matches. Discovery cannot know what a third-party tool
    /// does, so it assumes the worst: external side effects and an approval requirement.
    /// <para>
    /// Note what approval means with the shipped gate: <see cref="AutoApproveGate"/> approves
    /// automatically, so this flag buys an audit row (<c>ApprovalId = "auto"</c> in
    /// <c>ApprovalProps</c>), not a control point. A deployment that wants the call blocked until a
    /// human answers must replace the gate — <c>services.Replace(ServiceDescriptor.Singleton&lt;IApprovalGate, DenyAllGate&gt;())</c>
    /// or a custom <see cref="IApprovalGate"/>.
    /// </para>
    /// </summary>
    public LlmToolSafety DefaultSafety { get; init; } = new()
    {
        SideEffect = ToolSideEffect.External,
        Cost = ToolCostClass.Cheap,
        RequiresApproval = true,
    };
}

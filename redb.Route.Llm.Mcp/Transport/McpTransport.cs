namespace redb.Route.Llm.Mcp.Transport;

/// <summary>Transport flavor for an MCP server.</summary>
public enum McpTransportKind
{
    /// <summary>Spawn an external process and exchange newline-delimited JSON over stdin/stdout.</summary>
    Stdio,

    /// <summary>POST JSON-RPC requests to a base URL; receive notifications via SSE.</summary>
    HttpSse
}

/// <summary>
/// Configuration for an MCP server transport. Built via <see cref="McpTransport.Stdio"/>
/// or <see cref="McpTransport.Http"/> and passed to <see cref="McpServiceCollectionExtensions.AddMcpServer"/>.
/// </summary>
public sealed class McpTransport
{
    /// <summary>Selected transport kind.</summary>
    public McpTransportKind Kind { get; init; }

    // ── stdio ──
    /// <summary>Executable to spawn (stdio transport). Resolved against PATH if not absolute.</summary>
    public string? Command { get; init; }

    /// <summary>Command-line arguments (stdio transport).</summary>
    public IReadOnlyList<string> Arguments { get; init; } = [];

    /// <summary>Additional environment variables for the spawned process (stdio transport).</summary>
    public IReadOnlyDictionary<string, string> Environment { get; init; }
        = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Working directory for the spawned process (stdio transport). Null = caller's CWD.</summary>
    public string? WorkingDirectory { get; init; }

    // ── http+sse ──
    /// <summary>Base URL for HTTP+SSE transport (e.g. <c>https://mcp.example.com/</c>).</summary>
    public string? BaseUrl { get; init; }

    /// <summary>Optional bearer token for HTTP+SSE transport.</summary>
    public string? BearerToken { get; init; }

    /// <summary>Builds an stdio transport configuration.</summary>
    /// <param name="command">Executable to spawn.</param>
    /// <param name="arguments">Optional command-line arguments.</param>
    /// <param name="environment">Optional additional environment variables.</param>
    /// <param name="workingDirectory">Optional working directory.</param>
    public static McpTransport Stdio(
        string command,
        IReadOnlyList<string>? arguments = null,
        IReadOnlyDictionary<string, string>? environment = null,
        string? workingDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        return new McpTransport
        {
            Kind = McpTransportKind.Stdio,
            Command = command,
            Arguments = arguments ?? [],
            Environment = environment ?? new Dictionary<string, string>(StringComparer.Ordinal),
            WorkingDirectory = workingDirectory,
        };
    }

    /// <summary>Builds an HTTP+SSE transport configuration.</summary>
    /// <param name="baseUrl">Base URL of the MCP server.</param>
    /// <param name="bearerToken">Optional bearer token for the <c>Authorization</c> header.</param>
    public static McpTransport Http(string baseUrl, string? bearerToken = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);
        return new McpTransport
        {
            Kind = McpTransportKind.HttpSse,
            BaseUrl = baseUrl,
            BearerToken = bearerToken,
        };
    }
}

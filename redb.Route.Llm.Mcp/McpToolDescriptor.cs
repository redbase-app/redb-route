using System.Text.RegularExpressions;
using redb.Route.Abstractions;
using redb.Route.Llm.Abstractions.Tools;
using redb.Route.Llm.Mcp.Protocol;

namespace redb.Route.Llm.Mcp;

/// <summary>
/// <see cref="ILlmToolDescriptor"/> backed by a remote MCP tool. Capability built once
/// from <c>tools/list</c> metadata; <see cref="BuildEndpointUri"/> returns the
/// precomputed <c>mcp://server/tool</c> URI so dispatch is constant-time.
/// </summary>
public sealed class McpToolDescriptor : ILlmToolDescriptor
{
    /// <summary>Maximum length of an LLM-facing tool name (Anthropic / OpenAI cap).</summary>
    public const int MaxToolNameLength = 64;

    /// <summary>Maximum length of the server-prefix segment.</summary>
    public const int MaxServerPrefixLength = 24;

    /// <summary>Maximum length of the tool-name segment.</summary>
    public const int MaxToolPartLength = 36;

    /// <summary>Separator between sanitized server prefix and tool name.</summary>
    public const string Separator = "__";

    private static readonly Regex InvalidChars = new("[^a-zA-Z0-9_]", RegexOptions.Compiled);
    private static readonly Regex StartsWithDigit = new("^[0-9]", RegexOptions.Compiled);

    /// <inheritdoc />
    public LlmToolCapability Capability { get; }

    /// <summary>The raw server-side tool name (as returned by <c>tools/list</c>).</summary>
    public string RawToolName { get; }

    /// <summary>Logical MCP server name.</summary>
    public string ServerName { get; }

    /// <summary>Precomputed <c>mcp://server/tool</c> dispatch URI.</summary>
    public string EndpointUri { get; }

    /// <summary>Creates a new descriptor — usually built by <see cref="McpDiscoveryService"/>.</summary>
    /// <param name="serverName">Logical server name.</param>
    /// <param name="rawToolName">Raw server-side tool name.</param>
    /// <param name="capability">Capability projected to the model.</param>
    public McpToolDescriptor(string serverName, string rawToolName, LlmToolCapability capability)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverName);
        ArgumentException.ThrowIfNullOrWhiteSpace(rawToolName);
        ArgumentNullException.ThrowIfNull(capability);

        ServerName = serverName;
        RawToolName = rawToolName;
        Capability = capability;
        EndpointUri = $"mcp://{serverName}/{rawToolName}";
    }

    /// <inheritdoc />
    public string BuildEndpointUri(string inputJson, IExchange parentExchange) => EndpointUri;

    /// <summary>
    /// Builds an LLM-facing tool name <c>{server}{Separator}{tool}</c> truncated to fit
    /// the 64-char model limit, with non-alphanumerics replaced by <c>_</c>.
    /// </summary>
    /// <param name="serverName">Logical server name.</param>
    /// <param name="toolName">Raw tool name.</param>
    /// <returns>Sanitized model-facing name (≤ <see cref="MaxToolNameLength"/>).</returns>
    public static string BuildModelFacingName(string serverName, string toolName)
    {
        var server = Sanitize(serverName, MaxServerPrefixLength);
        var tool = Sanitize(toolName, MaxToolPartLength);

        var combined = $"{server}{Separator}{tool}";
        if (combined.Length > MaxToolNameLength)
            combined = combined[..MaxToolNameLength];

        if (StartsWithDigit.IsMatch(combined))
            combined = "_" + combined[..(MaxToolNameLength - 1)];

        return combined;
    }

    /// <summary>Builds the JSON-Schema string for a descriptor — falls back to a permissive object when missing.</summary>
    /// <param name="definition">Tool definition received from <c>tools/list</c>.</param>
    public static string BuildInputSchema(ToolDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return definition.InputSchema?.ToJsonString() ?? """{"type":"object","additionalProperties":true}""";
    }

    private static string Sanitize(string value, int maxLength)
    {
        var cleaned = InvalidChars.Replace(value, "_");
        if (cleaned.Length > maxLength) cleaned = cleaned[..maxLength];
        return cleaned;
    }
}

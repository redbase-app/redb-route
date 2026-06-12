using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace redb.Route.Llm.Mcp.Protocol;

/// <summary>
/// MCP protocol DTOs and JSON-RPC envelope types. Pinned to MCP protocol version
/// <c>2024-11-05</c> — the dialect Serena, Claude Desktop, and the official Anthropic
/// servers all speak.
/// </summary>
public static class McpProtocol
{
    /// <summary>The MCP protocol version this client advertises during <c>initialize</c>.</summary>
    public const string ProtocolVersion = "2024-11-05";

    /// <summary>JSON-RPC 2.0 protocol identifier.</summary>
    public const string JsonRpcVersion = "2.0";

    /// <summary>Shared JSON serialization options. <see cref="JsonIgnoreCondition.WhenWritingNull"/> matches MCP-server expectations (no nulls on the wire).</summary>
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };
}

/// <summary>JSON-RPC 2.0 request envelope.</summary>
public sealed class JsonRpcRequest
{
    /// <summary>Always <c>"2.0"</c>.</summary>
    [JsonPropertyName("jsonrpc")]
    public string JsonRpc { get; set; } = McpProtocol.JsonRpcVersion;

    /// <summary>Correlation id matching the eventual <see cref="JsonRpcResponse"/>.</summary>
    [JsonPropertyName("id")]
    public long Id { get; set; }

    /// <summary>RPC method name (e.g. <c>"tools/call"</c>).</summary>
    [JsonPropertyName("method")]
    public string Method { get; set; } = "";

    /// <summary>Method-specific parameters as a JSON object.</summary>
    [JsonPropertyName("params")]
    public JsonNode? Params { get; set; }
}

/// <summary>JSON-RPC 2.0 response envelope.</summary>
public sealed class JsonRpcResponse
{
    /// <summary>Always <c>"2.0"</c>.</summary>
    [JsonPropertyName("jsonrpc")]
    public string JsonRpc { get; set; } = McpProtocol.JsonRpcVersion;

    /// <summary>Correlation id matching the originating <see cref="JsonRpcRequest"/>.</summary>
    [JsonPropertyName("id")]
    public long? Id { get; set; }

    /// <summary>Successful method result. Mutually exclusive with <see cref="Error"/>.</summary>
    [JsonPropertyName("result")]
    public JsonNode? Result { get; set; }

    /// <summary>Error envelope when the call failed.</summary>
    [JsonPropertyName("error")]
    public JsonRpcError? Error { get; set; }
}

/// <summary>JSON-RPC error object.</summary>
public sealed class JsonRpcError
{
    /// <summary>Numeric error code per JSON-RPC convention.</summary>
    [JsonPropertyName("code")]
    public int Code { get; set; }

    /// <summary>Human-readable error message.</summary>
    [JsonPropertyName("message")]
    public string Message { get; set; } = "";

    /// <summary>Optional structured error data.</summary>
    [JsonPropertyName("data")]
    public JsonNode? Data { get; set; }
}

/// <summary>JSON-RPC 2.0 server-initiated notification (no response expected).</summary>
public sealed class JsonRpcNotification
{
    /// <summary>Always <c>"2.0"</c>.</summary>
    [JsonPropertyName("jsonrpc")]
    public string JsonRpc { get; set; } = McpProtocol.JsonRpcVersion;

    /// <summary>Notification method name (e.g. <c>"notifications/tools/list_changed"</c>).</summary>
    [JsonPropertyName("method")]
    public string Method { get; set; } = "";

    /// <summary>Notification payload.</summary>
    [JsonPropertyName("params")]
    public JsonNode? Params { get; set; }
}

/// <summary>Result of the <c>initialize</c> handshake.</summary>
public sealed class InitializeResult
{
    /// <summary>Protocol version the server has agreed to speak.</summary>
    [JsonPropertyName("protocolVersion")]
    public string ProtocolVersion { get; set; } = "";

    /// <summary>Server identification (name/version).</summary>
    [JsonPropertyName("serverInfo")]
    public ServerInfo? ServerInfo { get; set; }

    /// <summary>Server capability flags (verbatim — we don't interpret beyond logging).</summary>
    [JsonPropertyName("capabilities")]
    public JsonNode? Capabilities { get; set; }
}

/// <summary>Server identification block returned during <c>initialize</c>.</summary>
public sealed class ServerInfo
{
    /// <summary>Server name (free-form, e.g. <c>"serena"</c>).</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    /// <summary>Server version string.</summary>
    [JsonPropertyName("version")]
    public string Version { get; set; } = "";
}

/// <summary>One tool entry as returned by <c>tools/list</c>.</summary>
public sealed class ToolDefinition
{
    /// <summary>Server-side tool name (raw, before redb sanitisation).</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    /// <summary>Human-readable description shown to the model.</summary>
    [JsonPropertyName("description")]
    public string? Description { get; set; }

    /// <summary>JSON Schema for the tool's input arguments.</summary>
    [JsonPropertyName("inputSchema")]
    public JsonNode? InputSchema { get; set; }
}

/// <summary>Result of <c>tools/list</c>.</summary>
public sealed class ListToolsResult
{
    /// <summary>The list of tools the server exposes.</summary>
    [JsonPropertyName("tools")]
    public List<ToolDefinition> Tools { get; set; } = [];
}

/// <summary>Result of <c>tools/call</c>.</summary>
public sealed class CallToolResult
{
    /// <summary>Content blocks produced by the tool (text, images, embedded resources).</summary>
    [JsonPropertyName("content")]
    public JsonNode? Content { get; set; }

    /// <summary>True when the tool reported an error condition.</summary>
    [JsonPropertyName("isError")]
    public bool IsError { get; set; }
}

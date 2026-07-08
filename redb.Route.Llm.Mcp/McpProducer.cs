using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using redb.Route.Abstractions;
using redb.Route.Telemetry;

namespace redb.Route.Llm.Mcp;

/// <summary>
/// MCP producer — translates an exchange into an MCP <c>tools/call</c> RPC.
/// The exchange body is interpreted as JSON arguments; the result's
/// <c>content[]</c> array is serialized back to a JSON string in
/// <see cref="IExchange.In"/>.
/// </summary>
public sealed class McpProducer : IProducer
{
    private readonly McpEndpoint _endpoint;
    private readonly McpEndpointOptions _options;

    /// <summary>Creates a new MCP producer.</summary>
    /// <param name="endpoint">Owning endpoint.</param>
    /// <param name="options">Endpoint options.</param>
    public McpProducer(McpEndpoint endpoint, McpEndpointOptions options)
    {
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <inheritdoc />
    public Task Start(CancellationToken ct = default) => Task.CompletedTask;

    /// <inheritdoc />
    public Task Stop(CancellationToken ct = default) => Task.CompletedTask;

    /// <inheritdoc />
    public async Task Process(IExchange exchange, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(exchange);

        using var activity = RouteActivitySource.Source.StartActivity(
            $"mcp.tools/call {_endpoint.ServerName}/{_endpoint.ToolName}",
            ActivityKind.Client);

        if (activity is { IsAllDataRequested: true })
        {
            activity.SetTag("rpc.system", "mcp");
            activity.SetTag("rpc.service", _endpoint.ServerName);
            activity.SetTag("rpc.method", _endpoint.ToolName);
        }

        var client = _endpoint.Registry.GetClient(_endpoint.ServerName)
            ?? throw new McpException($"MCP server '{_endpoint.ServerName}' is not registered or has been disposed.");

        if (client.Status is McpClientStatus.Dead)
            throw new McpException($"MCP server '{_endpoint.ServerName}' is dead. Toolset will refresh on restart.");

        var arguments = ParseArguments(exchange.In.Body);

        using var linkedCts = CreateLinkedCts(ct, _options.CallTimeoutMs);

        try
        {
            var result = await client.CallToolAsync(_endpoint.ToolName, arguments, linkedCts.Token).ConfigureAwait(false);

            // Replace the body with the serialized content array. The agent engine's
            // SerializeReply path handles strings as raw JSON.
            var json = result.Content?.ToJsonString() ?? "[]";
            exchange.In.Body = json;
            exchange.In.ContentType = "application/json";
            exchange.In.Headers["Mcp-Server"] = _endpoint.ServerName;
            exchange.In.Headers["Mcp-Tool"] = _endpoint.ToolName;
            if (result.IsError)
                exchange.In.Headers["Mcp-IsError"] = "true";
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"MCP tools/call to '{_endpoint.ServerName}/{_endpoint.ToolName}' timed out after {_options.CallTimeoutMs}ms.");
        }
    }

    private static JsonNode? ParseArguments(object? body)
    {
        return body switch
        {
            null => null,
            JsonNode node => node,
            string s when string.IsNullOrWhiteSpace(s) => null,
            string s => SafeParse(s),
            _ => SafeParse(JsonSerializer.Serialize(body)),
        };
    }

    private static JsonNode? SafeParse(string json)
    {
        try { return JsonNode.Parse(json); }
        catch (JsonException) { return null; }
    }

    private static CancellationTokenSource CreateLinkedCts(CancellationToken ct, int timeoutMs)
    {
        if (timeoutMs <= 0)
            return CancellationTokenSource.CreateLinkedTokenSource(ct);
        var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(timeoutMs);
        return linked;
    }
}

using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using redb.Route.Llm.Mcp.Protocol;

namespace redb.Route.Llm.Mcp.Protocol;

/// <summary>
/// Shared client logic: per-id <see cref="TaskCompletionSource{TResult}"/> registry,
/// monotonic id generator, response demux, <c>notifications/cancelled</c> emission on
/// CT trip. Subclasses provide the byte-level transport (<see cref="SendFrameAsync"/> +
/// the read pump that calls <see cref="OnFrameReceived"/>).
/// </summary>
public abstract class McpClientBase : IMcpClient
{
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonRpcResponse>> _pending = new();
    private long _nextId;

    /// <summary>Logger used by the client and its read pump.</summary>
    protected ILogger Logger { get; }

    /// <inheritdoc />
    public string ServerName { get; }

    /// <inheritdoc />
    public McpClientStatus Status { get; protected set; } = McpClientStatus.Idle;

    /// <inheritdoc />
    public InitializeResult? Initialize { get; private set; }

    /// <inheritdoc />
    public event EventHandler? ToolsChanged;

    /// <summary>Initializes the base client with the given server name and logger.</summary>
    /// <param name="serverName">Logical server identifier (registry key).</param>
    /// <param name="logger">Logger.</param>
    protected McpClientBase(string serverName, ILogger logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverName);
        ArgumentNullException.ThrowIfNull(logger);
        ServerName = serverName;
        Logger = logger;
    }

    /// <inheritdoc />
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        Status = McpClientStatus.Connecting;
        try
        {
            await StartTransportAsync(cancellationToken).ConfigureAwait(false);

            var initParams = new JsonObject
            {
                ["protocolVersion"] = McpProtocol.ProtocolVersion,
                ["capabilities"] = new JsonObject(),
                ["clientInfo"] = new JsonObject
                {
                    ["name"] = "redb.Route.Llm.Mcp",
                    ["version"] = "3.1.1",
                },
            };

            var resultNode = await SendRequestAsync("initialize", initParams, cancellationToken).ConfigureAwait(false);
            Initialize = resultNode?.Deserialize<InitializeResult>(McpProtocol.JsonOptions);

            if (Initialize is null)
                throw new McpException($"Server '{ServerName}' returned an empty initialize result.");

            if (!string.IsNullOrEmpty(Initialize.ProtocolVersion)
                && Initialize.ProtocolVersion != McpProtocol.ProtocolVersion)
            {
                Logger.LogWarning(
                    "MCP server {Server} negotiated protocol version {Negotiated} (we sent {Sent}). Continuing.",
                    ServerName, Initialize.ProtocolVersion, McpProtocol.ProtocolVersion);
            }

            // Notify the server that we've finished the handshake.
            await SendNotificationAsync("notifications/initialized", null, cancellationToken).ConfigureAwait(false);

            Status = McpClientStatus.Healthy;
            Logger.LogInformation(
                "MCP client '{Server}' connected. ServerInfo={ServerInfo}",
                ServerName, Initialize.ServerInfo?.Name);
        }
        catch
        {
            Status = McpClientStatus.Dead;
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ToolDefinition>> ListToolsAsync(CancellationToken cancellationToken = default)
    {
        var resultNode = await SendRequestAsync("tools/list", null, cancellationToken).ConfigureAwait(false);
        var listed = resultNode?.Deserialize<ListToolsResult>(McpProtocol.JsonOptions);
        return listed?.Tools ?? [];
    }

    /// <inheritdoc />
    public async Task<CallToolResult> CallToolAsync(string toolName, JsonNode? arguments, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);

        var callParams = new JsonObject
        {
            ["name"] = toolName,
            ["arguments"] = arguments ?? new JsonObject(),
        };

        var resultNode = await SendRequestAsync("tools/call", callParams, cancellationToken).ConfigureAwait(false);
        return resultNode?.Deserialize<CallToolResult>(McpProtocol.JsonOptions)
               ?? new CallToolResult { Content = new JsonArray(), IsError = false };
    }

    /// <inheritdoc />
    public virtual async ValueTask DisposeAsync()
    {
        // Fail any pending requests so callers don't hang.
        foreach (var (_, tcs) in _pending)
            tcs.TrySetException(new McpException($"MCP client '{ServerName}' was disposed."));
        _pending.Clear();

        try { await StopTransportAsync().ConfigureAwait(false); }
        catch (Exception ex) { Logger.LogDebug(ex, "MCP {Server} transport stop threw.", ServerName); }

        Status = McpClientStatus.Dead;
        GC.SuppressFinalize(this);
    }

    // ── Transport hooks (subclass) ──────────────────────────────────────────

    /// <summary>Brings up the byte-level transport (e.g. spawn process, open HTTP/SSE channels).</summary>
    protected abstract Task StartTransportAsync(CancellationToken cancellationToken);

    /// <summary>Tears the transport down. Should not throw on already-stopped state.</summary>
    protected abstract Task StopTransportAsync();

    /// <summary>Writes a single JSON frame to the server. Implementations must serialize concurrent writes.</summary>
    /// <param name="frameJson">Already-serialized JSON-RPC frame (no trailing newline — the transport adds it if needed).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    protected abstract Task SendFrameAsync(string frameJson, CancellationToken cancellationToken);

    /// <summary>Called by the transport's read pump for every received frame (response or notification).</summary>
    /// <param name="frameJson">Raw JSON text — newline already stripped, single message.</param>
    protected void OnFrameReceived(string frameJson)
    {
        if (string.IsNullOrWhiteSpace(frameJson)) return;

        JsonNode? root;
        try { root = JsonNode.Parse(frameJson); }
        catch (JsonException ex)
        {
            Logger.LogTrace(ex, "MCP {Server} skipping non-JSON frame: {Frame}", ServerName, frameJson);
            return;
        }
        if (root is not JsonObject obj) return;

        // Response carries an "id"; notification carries a "method" but no "id".
        if (obj.TryGetPropertyValue("id", out var idNode) && idNode is not null)
        {
            var resp = obj.Deserialize<JsonRpcResponse>(McpProtocol.JsonOptions);
            if (resp?.Id is { } id && _pending.TryRemove(id, out var tcs))
                tcs.TrySetResult(resp);
            return;
        }

        if (obj.TryGetPropertyValue("method", out var methodNode) && methodNode is not null)
        {
            var method = methodNode.GetValue<string>();
            HandleNotification(method);
        }
    }

    /// <summary>Convenience: invoked by transports when the connection drops outside our control.</summary>
    /// <param name="reason">Optional human-readable reason for logging.</param>
    protected void OnTransportFailed(string? reason)
    {
        Logger.LogWarning("MCP {Server} transport failed: {Reason}. Failing {Pending} pending request(s).",
            ServerName, reason ?? "unknown", _pending.Count);

        // Mark dead so producers / registry consumers can short-circuit subsequent
        // calls instead of blocking on a transport that no longer exists. The
        // McpDiscoveryService can spin up a fresh client on its own schedule.
        Status = McpClientStatus.Dead;

        foreach (var (_, tcs) in _pending)
            tcs.TrySetException(new McpException($"MCP client '{ServerName}' transport failed: {reason}"));
        _pending.Clear();
    }

    // ── Internals ───────────────────────────────────────────────────────────

    private async Task<JsonNode?> SendRequestAsync(string method, JsonNode? @params, CancellationToken cancellationToken)
    {
        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonRpcResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

        var request = new JsonRpcRequest { Id = id, Method = method, Params = @params };
        var json = JsonSerializer.Serialize(request, McpProtocol.JsonOptions);

        await using var ctReg = cancellationToken.Register(() =>
        {
            if (_pending.TryRemove(id, out var t))
            {
                t.TrySetCanceled(cancellationToken);
                _ = SendCancelNotificationAsync(id);
            }
        });

        try
        {
            await SendFrameAsync(json, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _pending.TryRemove(id, out _);
            throw;
        }

        var response = await tcs.Task.ConfigureAwait(false);
        if (response.Error is not null)
            throw new McpException(
                $"MCP server '{ServerName}' returned error for {method}: {response.Error.Code} {response.Error.Message}");
        return response.Result;
    }

    private async Task SendNotificationAsync(string method, JsonNode? @params, CancellationToken cancellationToken)
    {
        var note = new JsonRpcNotification { Method = method, Params = @params };
        var json = JsonSerializer.Serialize(note, McpProtocol.JsonOptions);
        await SendFrameAsync(json, cancellationToken).ConfigureAwait(false);
    }

    private async Task SendCancelNotificationAsync(long requestId)
    {
        try
        {
            var @params = new JsonObject { ["requestId"] = requestId, ["reason"] = "client cancelled" };
            await SendNotificationAsync("notifications/cancelled", @params, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.LogTrace(ex, "MCP {Server} failed to send cancel notification for id {Id}.", ServerName, requestId);
        }
    }

    private void HandleNotification(string method)
    {
        switch (method)
        {
            case "notifications/tools/list_changed":
                Logger.LogInformation("MCP {Server} reported tools/list_changed.", ServerName);
                ToolsChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "notifications/initialized":
                // Client→server only; ignore if we ever see it bouncing back.
                break;
            default:
                Logger.LogTrace("MCP {Server} received notification {Method}.", ServerName, method);
                break;
        }
    }
}

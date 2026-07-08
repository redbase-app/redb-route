using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Logging;
using redb.Route.Llm.Mcp.Protocol;

namespace redb.Route.Llm.Mcp.Transport;

/// <summary>
/// HTTP + SSE transport. POSTs JSON-RPC requests to the base URL, opens a parallel
/// SSE channel (GET) for server-initiated frames (notifications, tools/list_changed).
/// Bearer auth via <see cref="McpTransport.BearerToken"/>.
/// </summary>
public sealed class HttpSseMcpClient : McpClientBase
{
    private readonly McpTransport _transport;
    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;

    private CancellationTokenSource? _sseCts;
    private Task? _ssePump;

    /// <summary>Creates a new HTTP+SSE MCP client.</summary>
    /// <param name="serverName">Logical server name.</param>
    /// <param name="transport">HTTP+SSE transport configuration.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="httpClient">Optional HTTP client (DI-managed); a private one is created if null.</param>
    public HttpSseMcpClient(string serverName, McpTransport transport, ILogger logger, HttpClient? httpClient = null)
        : base(serverName, logger)
    {
        ArgumentNullException.ThrowIfNull(transport);
        if (transport.Kind != McpTransportKind.HttpSse)
            throw new ArgumentException("HttpSseMcpClient requires an HTTP+SSE transport.", nameof(transport));
        if (string.IsNullOrWhiteSpace(transport.BaseUrl))
            throw new ArgumentException("HTTP+SSE transport requires a BaseUrl.", nameof(transport));
        _transport = transport;

        if (httpClient is not null)
        {
            _http = httpClient;
            _ownsHttpClient = false;
        }
        else
        {
            _http = new HttpClient { BaseAddress = new Uri(transport.BaseUrl, UriKind.Absolute) };
            _ownsHttpClient = true;
        }

        if (!string.IsNullOrEmpty(transport.BearerToken))
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", transport.BearerToken);
    }

    /// <inheritdoc />
    protected override Task StartTransportAsync(CancellationToken cancellationToken)
    {
        _sseCts = new CancellationTokenSource();
        _ssePump = Task.Run(() => SsePumpAsync(_sseCts.Token));
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    protected override async Task StopTransportAsync()
    {
        try { _sseCts?.Cancel(); } catch { /* ignored */ }
        try { if (_ssePump is not null) await _ssePump.ConfigureAwait(false); }
        catch { /* pump exceptions surface via OnTransportFailed */ }
        _ssePump = null;
        _sseCts?.Dispose();
        _sseCts = null;

        if (_ownsHttpClient)
            _http.Dispose();
    }

    /// <inheritdoc />
    protected override async Task SendFrameAsync(string frameJson, CancellationToken cancellationToken)
    {
        using var content = new StringContent(frameJson, Encoding.UTF8, "application/json");
        using var response = await _http.PostAsync(_transport.BaseUrl, content, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        // Some MCP servers return the JSON-RPC response inline; others stream via SSE.
        // Fold inline replies through the same demux path.
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(body) && body.AsSpan().TrimStart().Length > 0 && body.AsSpan().TrimStart()[0] == '{')
            OnFrameReceived(body);
    }

    private async Task SsePumpAsync(CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, _transport.BaseUrl);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

            using var response = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                Logger.LogTrace("MCP {Server} SSE channel returned {Status}; skipping pump.", ServerName, response.StatusCode);
                return;
            }

            using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var reader = new StreamReader(stream, Encoding.UTF8);

            var dataBuffer = new StringBuilder();
            while (!ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (line is null) break;

                if (line.Length == 0)
                {
                    if (dataBuffer.Length > 0)
                    {
                        var frame = dataBuffer.ToString();
                        dataBuffer.Clear();
                        OnFrameReceived(frame);
                    }
                    continue;
                }

                if (line.StartsWith("data:", StringComparison.Ordinal))
                {
                    if (dataBuffer.Length > 0) dataBuffer.Append('\n');
                    dataBuffer.Append(line.AsSpan(5).TrimStart());
                }
                // Other SSE fields (event:, id:, retry:) are ignored — MCP only uses data:.
            }
        }
        catch (OperationCanceledException) { /* normal shutdown */ }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "MCP {Server} SSE pump terminated.", ServerName);
            OnTransportFailed($"SSE pump error: {ex.Message}");
        }
    }
}

using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.Core;

namespace redb.Route.WebSocket;

/// <summary>
/// WebSocket consumer. Embedded Kestrel-based WebSocket server that accepts connections,
/// receives message frames, and dispatches them into the route processor pipeline.
/// <para>Supports text and binary frames, subprotocol negotiation, MaxConnections throttling,
/// and InOut exchange pattern for sending response frames back.</para>
/// </summary>
public sealed class WsConsumer : IConsumer
{
    private readonly WsEndpoint _endpoint;
    public IEndpoint Endpoint => _endpoint;
    private readonly IProcessor _processor;
    private readonly WsEndpointOptions _options;
    private readonly Encoding _encoding;
    private WebApplication? _app;
    private Task? _runTask;
    private long _processedCount;
    private readonly ConcurrentDictionary<string, System.Net.WebSockets.WebSocket> _clients = new();
    private readonly SemaphoreSlim? _connectionSemaphore;
    private CancellationTokenSource? _cts;
    private readonly InflightDrainGuard _drain = new();

    private ILogger? _logger;

    /// <summary>Creates a WebSocket consumer.</summary>
    public WsConsumer(WsEndpoint endpoint, IProcessor processor, WsEndpointOptions options)
    {
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        _processor = processor ?? throw new ArgumentNullException(nameof(processor));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _encoding = Encoding.GetEncoding(options.Encoding);
        _logger = (endpoint.Component as ComponentBase)?.Logger;

        if (options.MaxConnections > 0)
            _connectionSemaphore = new SemaphoreSlim(options.MaxConnections, options.MaxConnections);
    }

    /// <summary>Number of messages successfully processed.</summary>
    public long ProcessedCount => Interlocked.Read(ref _processedCount);

    /// <summary>Number of currently connected WebSocket clients.</summary>
    public int ActiveConnections => _clients.Count;

    /// <summary>The base URL the server is listening on. Available after Start().</summary>
    public string? BaseUrl { get; private set; }

    /// <inheritdoc />
    public Task Start(CancellationToken ct = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _drain.Start(ct);

        try
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.ConfigureKestrel(ConfigureKestrel);
            builder.Logging.ClearProviders();

            _app = builder.Build();
            _app.UseWebSockets(new WebSocketOptions
            {
                KeepAliveInterval = _options.KeepAliveInterval > 0
                    ? TimeSpan.FromMilliseconds(_options.KeepAliveInterval)
                    : TimeSpan.Zero
            });

            var path = _endpoint.ConsumerPath;
            _app.Map(path, HandleRequest);

            _runTask = _app.StartAsync(_cts.Token);
            BaseUrl = BuildBaseUrl();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "WebSocket consumer Start failed on {Host}:{Port}", _options.Host, _options.Port);
            if (_app != null) { _app.DisposeAsync().AsTask().GetAwaiter().GetResult(); _app = null; }
            _cts?.Dispose(); _cts = null;
            _drain.Dispose();
            throw;
        }

        _logger ??= (_endpoint.Component as ComponentBase)?.Logger;
        _logger?.LogInformation("WebSocket consumer started: {Url}", BaseUrl);
        return _runTask;
    }

    /// <inheritdoc />
    public async Task Stop(CancellationToken ct = default)
    {
        // Signal cancellation to all handlers
        _cts?.Cancel();

        // Drain in-flight message processing before tearing down the server
        await _drain.DrainAsync(ct, _logger, "WebSocket").ConfigureAwait(false);

        // Stop Kestrel — this triggers RequestAborted on all active connections,
        // which causes HandleWebSocket loops to exit cleanly.
        if (_app is not null)
        {
            try
            {
                using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await _app.StopAsync(stopCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            await _app.DisposeAsync().ConfigureAwait(false);
            _app = null;
        }

        if (_runTask is not null)
        {
            try { await _runTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            _runTask = null;
        }

        // Clean up any remaining client references
        foreach (var (id, ws) in _clients)
        {
            ws.Dispose();
            _clients.TryRemove(id, out _);
        }

        _cts?.Dispose();
        _cts = null;
        _drain.Dispose();
        _logger ??= (_endpoint.Component as ComponentBase)?.Logger;
        _logger?.LogInformation("WebSocket consumer stopped");
    }

    private void ConfigureKestrel(KestrelServerOptions kestrel)
    {
        var host = _options.Host;
        var port = _options.Port;

        if (_options.Ssl && !string.IsNullOrEmpty(_options.SslCertPath))
        {
            kestrel.Listen(IPAddress.Parse(host), port, listenOptions =>
            {
                listenOptions.UseHttps(_options.SslCertPath, _options.SslCertPassword);
            });
        }
        else
        {
            kestrel.Listen(IPAddress.Parse(host), port);
        }
    }

    private async Task HandleRequest(HttpContext httpContext)
    {
        if (!httpContext.WebSockets.IsWebSocketRequest)
        {
            httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
            await httpContext.Response.WriteAsync("WebSocket connections only.").ConfigureAwait(false);
            return;
        }

        // Throttle connections
        if (_connectionSemaphore is not null)
            await _connectionSemaphore.WaitAsync(httpContext.RequestAborted).ConfigureAwait(false);

        var connectionId = Guid.NewGuid().ToString("N");
        System.Net.WebSockets.WebSocket ws;

        try
        {
            ws = _options.SubProtocol is not null
                ? await httpContext.WebSockets.AcceptWebSocketAsync(_options.SubProtocol).ConfigureAwait(false)
                : await httpContext.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
        }
        catch
        {
            _connectionSemaphore?.Release();
            return;
        }

        _clients[connectionId] = ws;

        var remoteEp = httpContext.Connection.RemoteIpAddress is not null
            ? $"{httpContext.Connection.RemoteIpAddress}:{httpContext.Connection.RemotePort}"
            : "unknown";
        var localEp = httpContext.Connection.LocalIpAddress is not null
            ? $"{httpContext.Connection.LocalIpAddress}:{httpContext.Connection.LocalPort}"
            : "unknown";

        try
        {
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(httpContext.RequestAborted, _cts?.Token ?? CancellationToken.None);
            await HandleWebSocket(ws, connectionId, remoteEp, localEp, linkedCts.Token)
                .ConfigureAwait(false);
        }
        finally
        {
            _connectionSemaphore?.Release();
            ws.Dispose();
            _clients.TryRemove(connectionId, out _);
        }
    }

    private async Task HandleWebSocket(System.Net.WebSockets.WebSocket ws, string connectionId,
        string remoteEp, string localEp, CancellationToken ct)
    {
        var buffer = new byte[_options.ReceiveBufferSize];

        while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            byte[] data;
            WebSocketMessageType msgType;

            try
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await ws.ReceiveAsync(buffer, ct).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                        return;

                    ms.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                data = ms.ToArray();
                msgType = result.MessageType;
            }
            catch (OperationCanceledException) { return; }
            catch (WebSocketException) { return; }

            var exchange = BuildExchange(data, msgType, connectionId, remoteEp, localEp);

            _drain.Increment();
            try
            {
                try
                {
                    await _processor.Process(exchange, _drain.ProcessingToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "WebSocket message processing failed: remote={RemoteEndpoint}, type={MessageType}",
                        remoteEp, msgType);
                    exchange.Exception = ex;
                }

                // InOut: send response frame (use drain-safe token so response completes during drain)
                if (_options.InOut && exchange.Exception is null && exchange.HasOut && exchange.Out!.Body is not null)
                {
                    var (responseData, responseType) = ResolveResponseBody(exchange);
                    await ws.SendAsync(responseData, responseType, endOfMessage: true, _drain.ProcessingToken).ConfigureAwait(false);
                }

                Interlocked.Increment(ref _processedCount);

                await exchange.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                _drain.Decrement();
            }
        }
    }

    private IExchange BuildExchange(byte[] data, WebSocketMessageType msgType,
        string connectionId, string remoteEp, string localEp)
    {
        object body = msgType == WebSocketMessageType.Text
            ? _encoding.GetString(data)
            : data;

        var message = new Message(body);
        message.Headers[WsHeaders.RemoteAddress] = remoteEp;
        message.Headers[WsHeaders.LocalAddress] = localEp;
        message.Headers[WsHeaders.ConnectionId] = connectionId;
        message.Headers[WsHeaders.MessageType] = msgType == WebSocketMessageType.Text ? "Text" : "Binary";
        message.Headers[WsHeaders.ByteCount] = data.Length.ToString();
        message.Headers[WsHeaders.Ssl] = _options.Ssl.ToString();
        message.Headers[WsHeaders.Path] = _endpoint.ConsumerPath;

        if (_options.SubProtocol is not null)
            message.Headers[WsHeaders.SubProtocol] = _options.SubProtocol;

        var exchange = Exchange.Create(message, _endpoint.ScopeFactory);
        exchange.Pattern = _options.InOut ? ExchangePattern.InOut : ExchangePattern.InOnly;
        return exchange;
    }

    private (byte[] data, WebSocketMessageType type) ResolveResponseBody(IExchange exchange)
    {
        var outBody = exchange.Out!.Body!;
        return outBody switch
        {
            byte[] bytes => (bytes, WebSocketMessageType.Binary),
            string str => (_encoding.GetBytes(str), WebSocketMessageType.Text),
            _ => (_encoding.GetBytes(outBody.ToString()!), WebSocketMessageType.Text)
        };
    }

    private string BuildBaseUrl()
    {
        var scheme = _options.Ssl ? "wss" : "ws";
        var host = _options.Host == "0.0.0.0" ? "127.0.0.1" : _options.Host;
        return $"{scheme}://{host}:{_options.Port}";
    }
}

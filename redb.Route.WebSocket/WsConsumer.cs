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
using redb.Route.Http;

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
    private SharedHttpServerManager? _serverManager;
    private RouteRegistration? _registration;
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

    /// <summary>Ids of the currently connected clients, addressable by a server-mode producer.</summary>
    public IReadOnlyCollection<string> ConnectionIds => _clients.Keys.ToArray();

    /// <summary>The endpoint options, for the component's consumer registry.</summary>
    internal WsEndpointOptions EndpointOptions => _options;

    /// <summary>The path this consumer serves, for the component's consumer registry.</summary>
    internal string ConsumerPath => _endpoint.ConsumerPath;

    /// <summary>
    /// Pushes a frame into connected clients: one of them when <paramref name="connectionId"/> is
    /// given, all of them otherwise. Returns how many sockets it reached.
    /// </summary>
    internal async Task<int> SendToClients(byte[] data, WebSocketMessageType type,
        string? connectionId, CancellationToken ct)
    {
        var targets = connectionId is null
            ? _clients.ToArray()
            : _clients.TryGetValue(connectionId, out var one)
                ? [new KeyValuePair<string, System.Net.WebSockets.WebSocket>(connectionId, one)]
                : Array.Empty<KeyValuePair<string, System.Net.WebSockets.WebSocket>>();

        if (connectionId is not null && targets.Length == 0)
            throw new InvalidOperationException(
                $"No WebSocket client with connection id '{connectionId}' on {_endpoint.ConsumerPath}. " +
                "The client may have disconnected since the exchange that carried the id.");

        var sent = 0;
        foreach (var (id, socket) in targets)
        {
            if (socket.State != WebSocketState.Open) continue;
            try
            {
                await socket.SendAsync(data, type, endOfMessage: true, ct).ConfigureAwait(false);
                sent++;
            }
            catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException)
            {
                // One dead socket must not fail a broadcast to everyone else; it is dropped from
                // the registry and its handler's finally will finish the cleanup.
                _logger?.LogDebug(ex, "WebSocket push skipped a closed client: {ConnectionId}", id);
                _clients.TryRemove(id, out _);
            }
        }

        return sent;
    }

    /// <summary>The base URL the server is listening on. Available after Start().</summary>
    public string? BaseUrl { get; private set; }

    /// <inheritdoc />
    public async Task Start(CancellationToken ct = default)
    {
        // wss:// without a certificate anywhere refuses to bind — enforced once, by the shared
        // host, which is the only place that sees the endpoint, the connection factory and the
        // host default together.
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _drain.Start(ct);

        try
        {
            // One Kestrel per host:port, shared with HTTP, gRPC, SOAP and AS2 (Ф14 волна 1):
            // the endpoint asked for WebSocket support during its own Start, which runs before
            // any consumer, so the listener is built with the upgrade middleware in place.
            _serverManager = _endpoint.ServerManager;

            // Idempotent: the endpoint already asked for this during its own Start when the route
            // context owns the lifecycle. Repeating it here keeps a hand-built consumer (tests,
            // embedded use) working, and still fails loud if the listener came up without upgrades.
            _serverManager.EnableWebSockets(_options.Host, _options.Port,
                _options.KeepAliveInterval > 0
                    ? TimeSpan.FromMilliseconds(_options.KeepAliveInterval)
                    : null,
                _options.Ssl, _options.SslCertPath, _options.SslCertPassword);

            _registration = _serverManager.RegisterRoute(
                _options.Host, _options.Port, _endpoint.ConsumerPath, methods: null, HandleRequest,
                _options.Ssl, _options.SslCertPath, _options.SslCertPassword);

            // Visible to a server-mode producer only once the listener is up, so a push can never
            // find a consumer that is not actually serving.
            if (_endpoint.Component is WsComponent comp)
                comp.RegisterConsumer(this);

            await _serverManager.EnsureStarted(_options.Host, _options.Port, ct).ConfigureAwait(false);
            BaseUrl = _serverManager.GetBaseUrl(_options.Host, _options.Port) ?? BuildBaseUrl();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "WebSocket consumer Start failed on {Host}:{Port}", _options.Host, _options.Port);
            if (_endpoint.Component is WsComponent failed)
                failed.UnregisterConsumer(this);
            if (_registration is not null)
            {
                _serverManager?.UnregisterRoute(_registration);
                _registration = null;
            }
            _cts?.Dispose(); _cts = null;
            _drain.Dispose();
            throw;
        }

        _logger ??= (_endpoint.Component as ComponentBase)?.Logger;
        _logger?.LogInformation("WebSocket consumer started: {Url}{Path}", BaseUrl, _endpoint.ConsumerPath);
    }

    /// <inheritdoc />
    public async Task Stop(CancellationToken ct = default)
    {
        // A server-mode producer must stop finding us before the sockets start closing.
        if (_endpoint.Component is WsComponent comp)
            comp.UnregisterConsumer(this);

        // Signal cancellation to all handlers: the receive loops exit and stop taking new frames.
        _cts?.Cancel();

        // Drain in-flight message processing before the listener goes away.
        await _drain.DrainAsync(ct, _logger, "WebSocket").ConfigureAwait(false);

        // Give up our route; the listener itself stops only when the last route on it is gone,
        // because HTTP/gRPC/SOAP consumers may still be serving the same port.
        if (_registration is not null && _serverManager is not null)
        {
            _serverManager.UnregisterRoute(_registration);
            _registration = null;
            await _serverManager.StopIfEmpty(_options.Host, _options.Port, ct).ConfigureAwait(false);
        }

        // Close any sockets still open (their handlers already left the receive loop).
        foreach (var (id, ws) in _clients)
        {
            ws.Dispose();
            _clients.TryRemove(id, out _);
        }

        _cts?.Dispose();
        _cts = null;
        _connectionSemaphore?.Dispose();
        _drain.Dispose();
        _logger ??= (_endpoint.Component as ComponentBase)?.Logger;
        _logger?.LogInformation("WebSocket consumer stopped");
    }

    private async Task HandleRequest(HttpContext httpContext)
    {
        if (!httpContext.WebSockets.IsWebSocketRequest)
        {
            httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
            await httpContext.Response.WriteAsync("WebSocket connections only.").ConfigureAwait(false);
            return;
        }

        // Host-supplied authentication, before the upgrade: a rejected handshake never becomes a
        // socket, and an accepted one carries its principal into every exchange it produces.
        string? userId = null;
        if ((_endpoint.Component as WsComponent)?.Authenticate is { } authenticate)
        {
            var principal = await authenticate(httpContext).ConfigureAwait(false);
            if (principal is null)
            {
                httpContext.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            httpContext.User = principal;
            userId = principal.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                     ?? principal.Identity?.Name;
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
            await HandleWebSocket(ws, connectionId, remoteEp, localEp, userId, linkedCts.Token)
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
        string remoteEp, string localEp, string? userId, CancellationToken ct)
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

            // Pipeline statistics (MessagesIn/BytesIn/Errors) belong to the core: the routed
            // processor chain is wrapped in StatisticsProcessor, and recording here as well
            // double-counted every frame (the statistics-ownership audit).
            var exchange = BuildExchange(data, msgType, connectionId, remoteEp, localEp, userId);

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
                    if (exchange.Out.Body is IAsyncEnumerable<string> asyncStrings)
                    {
                        await foreach (var chunk in asyncStrings.WithCancellation(_drain.ProcessingToken).ConfigureAwait(false))
                        {
                            if (string.IsNullOrEmpty(chunk)) continue;
                            var bytes = _encoding.GetBytes(chunk);
                            await ws.SendAsync(bytes, WebSocketMessageType.Text,
                                endOfMessage: true, _drain.ProcessingToken).ConfigureAwait(false);
                        }
                    }
                    else
                    {
                        var (responseData, responseType) = ResolveResponseBody(exchange);
                        await ws.SendAsync(responseData, responseType, endOfMessage: true, _drain.ProcessingToken).ConfigureAwait(false);
                    }
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
        string connectionId, string remoteEp, string localEp, string? userId)
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

        if (userId is not null)
            message.Headers[WsHeaders.UserId] = userId;

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

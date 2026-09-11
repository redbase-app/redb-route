using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Telemetry;

namespace redb.Route.WebSocket;

/// <summary>
/// WebSocket producer. Connects to a remote WebSocket server using <see cref="ClientWebSocket"/>
/// and sends exchange body as text or binary frames.
/// In InOut mode, waits for a response frame and populates the exchange Out message.
/// </summary>
public sealed class WsProducer : ConnectableProducer
{
    private readonly WsEndpoint _endpoint;
    private readonly WsEndpointOptions _options;
    private readonly Encoding _encoding;
    private ClientWebSocket? _ws;
    private WsConsumer? _localConsumer;
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    /// <summary>Creates a WebSocket producer.</summary>
    public WsProducer(WsEndpoint endpoint, WsEndpointOptions options)
    {
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _encoding = Encoding.GetEncoding(options.Encoding);
    }

    /// <inheritdoc />
    protected override IEndpoint ProducerEndpoint => _endpoint;

    /// <inheritdoc />
    // The name goes into the "producer started" log line, so a ws://user:pass@host URI must not
    // carry its password through it.
    protected override string ProducerName =>
        $"ws:{_options.Mode}:{EndpointUri.Sanitize(_endpoint.BuildProducerUrl())}";

    /// <summary>The logged producer name, so a test can pin the masking.</summary>
    internal string DiagnosticName => ProducerName;

    /// <inheritdoc />
    protected override async Task ConnectAsync(CancellationToken ct)
    {
        if (_options.Mode == WsMode.Server)
        {
            // Push into the clients of the consumer on the same URI. It has to be running:
            // silently pushing into nothing would be worse than a refusal to start.
            _localConsumer = (_endpoint.Component as WsComponent)?.GetConsumer(
                    _options.Host, _options.Port, _endpoint.ConsumerPath)
                ?? throw new InvalidOperationException(
                    $"Server-mode producer needs a running WebSocket consumer on {_endpoint.Uri.ToMaskedUriString()}. " +
                    "Start From(\"ws:...\") on the same URI before sending to \"ws:...?mode=Server\".");
            return;
        }

        await ConnectWsAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    protected override async Task DisconnectAsync(CancellationToken ct)
    {
        _localConsumer = null;

        if (_ws is { State: WebSocketState.Open or WebSocketState.CloseReceived })
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Stopping", cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) { Logger?.LogDebug(ex, "WebSocket: error closing during stop"); }
        }
        _ws?.Dispose();
        _ws = null;
    }

    /// <inheritdoc />
    public override async Task Process(IExchange exchange, CancellationToken ct = default)
    {
        EnsureStarted();

        using var activity = RouteTelemetryExtensions.StartTransportSpan(
            $"ws send {_options.Mode}", ActivityKind.Producer,
            "messaging.system", "websocket",
            _endpoint.Uri.NormalizedKey,
            destination: _endpoint.ConsumerPath,
            operation: "send");

        // No catch/RecordError here: an exception flies into the core (ToProcessor / the
        // template), which records it against this endpoint - recording here as well
        // double-counted it (the statistics-ownership audit).
        if (_options.Mode == WsMode.Server)
            await ProcessServerMode(exchange, ct).ConfigureAwait(false);
        else
            await ProcessClientMode(exchange, ct).ConfigureAwait(false);

        SetExchangeHeaders(exchange);
    }

    /// <summary>Pushes the body into the clients of the local consumer (mode=Server).</summary>
    private async Task ProcessServerMode(IExchange exchange, CancellationToken ct)
    {
        var (data, msgType) = ResolveBody(exchange);
        var target = exchange.In.Headers.TryGetValue(WsHeaders.TargetConnection, out var raw)
                     && raw is string id && !string.IsNullOrEmpty(id)
            ? id
            : null;

        var sent = await _localConsumer!.SendToClients(data, msgType, target, ct).ConfigureAwait(false);

        // MessagesOut is the core's (ToProcessor counts the exchange once); the connector owns
        // only the wire bytes - one exchange can reach many sockets, so bytes follow the frames.
        for (var i = 0; i < sent; i++)
            _endpoint.RecordBytesOut(data.Length);
    }

    private async Task ProcessClientMode(IExchange exchange, CancellationToken ct)
    {
        // Reconnect if needed
        if (_ws is null or { State: not WebSocketState.Open })
        {
            if (_options.Reconnect)
                await ReconnectAsync(ct).ConfigureAwait(false);
            else
                throw new InvalidOperationException("WebSocket connection lost and reconnect is disabled.");
        }

        var (data, msgType) = ResolveBody(exchange);

        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _ws!.SendAsync(data, msgType, endOfMessage: true, ct).ConfigureAwait(false);
            _endpoint.RecordBytesOut(data.Length); // wire bytes only: MessagesOut belongs to the core

            // InOut: read response frame
            if (_options.InOut)
            {
                var (responseData, responseType) = await ReceiveMessageAsync(ct).ConfigureAwait(false);
                if (responseData is not null)
                {
                    _endpoint.RecordBytesIn(responseData.Length);
                    object body = responseType == WebSocketMessageType.Text
                        ? _encoding.GetString(responseData)
                        : responseData;

                    var outMsg = new Message(body);
                    outMsg.Headers[WsHeaders.MessageType] = responseType == WebSocketMessageType.Text ? "Text" : "Binary";
                    outMsg.Headers[WsHeaders.ByteCount] = responseData.Length.ToString();
                    exchange.Out = outMsg;
                }
            }
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private (byte[] data, WebSocketMessageType type) ResolveBody(IExchange exchange)
    {
        var body = exchange.In.Body;
        var wsType = _options.MessageType == WsMessageType.Binary
            ? WebSocketMessageType.Binary
            : WebSocketMessageType.Text;

        byte[] data = body switch
        {
            byte[] bytes => bytes,
            Stream s => ReadStream(s),
            string str => _encoding.GetBytes(str),
            null => [],
            _ => _encoding.GetBytes(body.ToString()!)
        };

        return (data, wsType);
    }

    private static byte[] ReadStream(Stream s)
    {
        if (s is MemoryStream ms) return ms.ToArray();
        using var temp = new MemoryStream();
        s.CopyTo(temp);
        return temp.ToArray();
    }

    private async Task<(byte[]? data, WebSocketMessageType type)> ReceiveMessageAsync(CancellationToken ct)
    {
        var buffer = new byte[_options.ReceiveBufferSize];
        using var ms = new MemoryStream();

        WebSocketReceiveResult result;
        do
        {
            result = await _ws!.ReceiveAsync(buffer, ct).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
                return (null, WebSocketMessageType.Close);

            ms.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);

        return (ms.ToArray(), result.MessageType);
    }

    private void SetExchangeHeaders(IExchange exchange)
    {
        exchange.In.Headers[WsHeaders.MessageType] = _options.MessageType.ToString();
        exchange.In.Headers[WsHeaders.Ssl] = _options.Ssl.ToString();
        if (_ws?.SubProtocol is not null)
            exchange.In.Headers[WsHeaders.SubProtocol] = _ws.SubProtocol;
    }

    private async Task ConnectWsAsync(CancellationToken ct)
    {
        _ws = new ClientWebSocket();

        if (_options.KeepAliveInterval > 0)
            _ws.Options.KeepAliveInterval = TimeSpan.FromMilliseconds(_options.KeepAliveInterval);

        _ws.Options.SetBuffer(_options.ReceiveBufferSize, _options.SendBufferSize);

        if (_options.SubProtocol is not null)
            _ws.Options.AddSubProtocol(_options.SubProtocol);

        // Explicit, never implied: a self-signed staging server is reachable only when the route
        // says so out loud.
        if (_options.TrustAllCertificates)
            _ws.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true;

        var uri = new Uri(_endpoint.BuildProducerUrl());

        using var cts = _options.ConnectTimeout > 0
            ? CancellationTokenSource.CreateLinkedTokenSource(ct)
            : null;
        cts?.CancelAfter(_options.ConnectTimeout);

        await _ws.ConnectAsync(uri, cts?.Token ?? ct).ConfigureAwait(false);
    }

    private async Task ReconnectAsync(CancellationToken ct)
    {
        var attempts = 0;
        var budget = _options.ReconnectTimeout > 0 ? Stopwatch.StartNew() : null;

        while (true)
        {
            attempts++;
            try
            {
                _ws?.Dispose();
                await ConnectWsAsync(ct).ConfigureAwait(false);
                return;
            }
            catch when (_options.MaxReconnectAttempts > 0 && attempts >= _options.MaxReconnectAttempts)
            {
                Logger?.LogError("WebSocket reconnect to {Host}:{Port} exhausted {Max} attempts",
                    _options.Host, _options.Port, _options.MaxReconnectAttempts);
                throw;
            }
            catch when (budget is not null && budget.ElapsedMilliseconds >= _options.ReconnectTimeout)
            {
                // Without a budget an exchange against a server that stays down never returns:
                // it does not fail, so dead-letter never fires and the route simply hangs.
                Logger?.LogError("WebSocket reconnect to {Host}:{Port} exhausted its {Timeout} ms budget after {Attempts} attempts",
                    _options.Host, _options.Port, _options.ReconnectTimeout, attempts);
                throw;
            }
            catch (Exception ex)
            {
                Logger?.LogWarning(ex, "WebSocket reconnect to {Host}:{Port} failed, attempt {Attempt}",
                    _options.Host, _options.Port, attempts);
                await Task.Delay(_options.ReconnectInterval, ct).ConfigureAwait(false);
            }
        }
    }
}

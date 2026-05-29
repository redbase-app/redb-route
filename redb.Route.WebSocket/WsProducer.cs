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
    protected override string ProducerName => $"ws:{_endpoint.BuildProducerUrl()}";

    /// <inheritdoc />
    protected override async Task ConnectAsync(CancellationToken ct)
    {
        await ConnectWsAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    protected override async Task DisconnectAsync(CancellationToken ct)
    {
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
            "ws send", ActivityKind.Producer,
            "messaging.system", "websocket",
            _endpoint.Uri.NormalizedKey,
            operation: "send");

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

            // InOut: read response frame
            if (_options.InOut)
            {
                var (responseData, responseType) = await ReceiveMessageAsync(ct).ConfigureAwait(false);
                if (responseData is not null)
                {
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

        SetExchangeHeaders(exchange);
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
            catch (Exception ex)
            {
                Logger?.LogWarning(ex, "WebSocket reconnect to {Host}:{Port} failed, attempt {Attempt}",
                    _options.Host, _options.Port, attempts);
                await Task.Delay(_options.ReconnectInterval, ct).ConfigureAwait(false);
            }
        }
    }
}

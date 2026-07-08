using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Telemetry;

namespace redb.Route.Tcp;

/// <summary>
/// TCP producer. Connects to a remote TCP server and sends exchange body data.
/// Supports Raw, TextLine, and LengthPrefixed framing.
/// In InOut mode, waits for a response and populates the exchange Out message.
/// </summary>
public sealed class TcpProducer : ConnectableProducer
{
    private readonly TcpEndpoint _endpoint;
    private readonly TcpEndpointOptions _options;
    private readonly Encoding _encoding;
    private TcpClient? _client;
    private Stream? _stream;
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    /// <summary>Creates a TCP producer.</summary>
    public TcpProducer(TcpEndpoint endpoint, TcpEndpointOptions options)
    {
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _encoding = Encoding.GetEncoding(options.Encoding);
    }

    /// <inheritdoc />
    protected override IEndpoint ProducerEndpoint => _endpoint;

    /// <inheritdoc />
    protected override string ProducerName => $"tcp:{_options.Host}:{_options.Port}";

    /// <inheritdoc />
    protected override async Task ConnectAsync(CancellationToken ct)
    {
        await ConnectTcpAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    protected override async Task DisconnectAsync(CancellationToken ct)
    {
        if (_stream is not null)
        {
            await _stream.DisposeAsync().ConfigureAwait(false);
            _stream = null;
        }
        _client?.Dispose();
        _client = null;
    }

    /// <inheritdoc />
    public override async Task Process(IExchange exchange, CancellationToken ct = default)
    {
        EnsureStarted();

        using var activity = RouteTelemetryExtensions.StartTransportSpan(
            $"tcp {_options.Host}:{_options.Port}", ActivityKind.Client,
            "network.transport", "tcp",
            _endpoint.Uri.NormalizedKey,
            destination: $"{_options.Host}:{_options.Port}");

        // Reconnect if needed
        if (_client is not { Connected: true })
        {
            if (_options.Reconnect)
                await ReconnectAsync(ct).ConfigureAwait(false);
            else
                throw new InvalidOperationException("TCP connection lost and reconnect is disabled.");
        }

        var data = ResolveBody(exchange);

        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Transparent retry on stale pooled connection.
            // Socket.Connected reflects state of the *last* I/O, so a connection torn
            // down between Process() calls (server timeout, KeepAlive probe, firewall RST)
            // is only detected when the next Write throws IOException/SocketException.
            // Same pattern as HttpClient connection pool revalidation and SqlConnection
            // pool validation: treat the first failure on a cached connection as expected,
            // reconnect once silently, retry. If the second attempt also fails, bubble up.
            try
            {
                await SendAndOptionallyReadAsync(exchange, data, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (_options.Reconnect && IsStaleConnectionError(ex))
            {
                Logger?.LogDebug(ex,
                    "TCP stale pooled connection to {Host}:{Port} detected; reconnecting and retrying once",
                    _options.Host, _options.Port);
                await ReconnectAsync(ct).ConfigureAwait(false);
                await SendAndOptionallyReadAsync(exchange, data, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            _sendLock.Release();
        }

        SetExchangeHeaders(exchange);
    }

    private async Task SendAndOptionallyReadAsync(IExchange exchange, byte[] data, CancellationToken ct)
    {
        await TcpCodec.WriteMessageAsync(_stream!, data, _options.Framing, _options.Delimiter, _encoding, ct)
            .ConfigureAwait(false);

        // InOut: read response
        if (_options.InOut)
        {
            var response = await TcpCodec.ReadMessageAsync(
                _stream!, _options.Framing, _options.Delimiter, _options.ReceiveBufferSize, ct)
                .ConfigureAwait(false);

            if (response is not null)
            {
                var outMsg = new Message(_options.Framing == TcpFraming.TextLine
                    ? _encoding.GetString(response)
                    : (object)response);
                outMsg.Headers[TcpHeaders.ByteCount] = response.Length.ToString();
                exchange.Out = outMsg;
            }
        }
    }

    private static bool IsStaleConnectionError(Exception ex)
    {
        // Unwrap IOException → SocketException; recognise the classic stale-pool codes.
        var socketEx = ex as SocketException ?? ex.InnerException as SocketException;
        if (socketEx is not null)
        {
            return socketEx.SocketErrorCode is
                SocketError.ConnectionAborted     // 10053 WSAECONNABORTED
                or SocketError.ConnectionReset    // 10054 WSAECONNRESET
                or SocketError.NotConnected       // 10057 WSAENOTCONN
                or SocketError.Shutdown           // 10058 WSAESHUTDOWN
                or SocketError.HostUnreachable
                or SocketError.NetworkReset
                or SocketError.TimedOut;
        }
        return ex is IOException or ObjectDisposedException;
    }

    private byte[] ResolveBody(IExchange exchange)
    {
        var body = exchange.In.Body;
        return body switch
        {
            byte[] bytes => bytes,
            Stream s => ReadStream(s),
            string str => _encoding.GetBytes(str),
            null => [],
            _ => _encoding.GetBytes(body.ToString()!)
        };
    }

    private static byte[] ReadStream(Stream s)
    {
        if (s is MemoryStream ms) return ms.ToArray();
        using var temp = new MemoryStream();
        s.CopyTo(temp);
        return temp.ToArray();
    }

    private void SetExchangeHeaders(IExchange exchange)
    {
        if (_client?.Client.RemoteEndPoint is IPEndPoint remote)
            exchange.In.Headers[TcpHeaders.RemoteAddress] = remote.ToString();
        if (_client?.Client.LocalEndPoint is IPEndPoint local)
            exchange.In.Headers[TcpHeaders.LocalAddress] = local.ToString();
        exchange.In.Headers[TcpHeaders.Framing] = _options.Framing.ToString();
        exchange.In.Headers[TcpHeaders.Ssl] = _options.Ssl.ToString();
    }

    private async Task ConnectTcpAsync(CancellationToken ct)
    {
        _client = new TcpClient
        {
            NoDelay = _options.NoDelay,
            ReceiveBufferSize = _options.ReceiveBufferSize,
            SendBufferSize = _options.SendBufferSize
        };
        _client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, _options.KeepAlive);

        using var cts = _options.ConnectTimeout > 0
            ? CancellationTokenSource.CreateLinkedTokenSource(ct)
            : null;
        cts?.CancelAfter(_options.ConnectTimeout);

        await _client.ConnectAsync(_options.Host, _options.Port, cts?.Token ?? ct).ConfigureAwait(false);

        Stream netStream = _client.GetStream();

        if (_options.Ssl)
        {
            var sslStream = new SslStream(netStream, leaveInnerStreamOpen: false);
            var host = _options.SslTargetHost ?? _options.Host;
            await sslStream.AuthenticateAsClientAsync(host).ConfigureAwait(false);
            netStream = sslStream;
        }

        _stream = netStream;
    }

    private async Task ReconnectAsync(CancellationToken ct)
    {
        var attempts = 0;
        while (true)
        {
            attempts++;
            try
            {
                if (_stream is not null) await _stream.DisposeAsync().ConfigureAwait(false);
                _client?.Dispose();
                await ConnectTcpAsync(ct).ConfigureAwait(false);
                return;
            }
            catch when (_options.MaxReconnectAttempts > 0 && attempts >= _options.MaxReconnectAttempts)
            {
                Logger?.LogError("TCP reconnect to {Host}:{Port} exhausted {Max} attempts",
                    _options.Host, _options.Port, _options.MaxReconnectAttempts);
                throw;
            }
            catch (Exception ex)
            {
                Logger?.LogWarning(ex, "TCP reconnect to {Host}:{Port} failed, attempt {Attempt}",
                    _options.Host, _options.Port, attempts);
                await Task.Delay(_options.ReconnectInterval, ct).ConfigureAwait(false);
            }
        }
    }
}

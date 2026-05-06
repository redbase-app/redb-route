using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.Core;

namespace redb.Route.Tcp;

/// <summary>
/// TCP consumer. Embedded TCP server that accepts connections, reads framed messages,
/// and dispatches them into the route processor pipeline.
/// <para>Supports configurable framing (Raw, TextLine, LengthPrefixed), TLS,
/// max connections, and InOut exchange patterns for synchronous response.</para>
/// </summary>
public sealed class TcpConsumer : IConsumer
{
    private readonly TcpEndpoint _endpoint;
    public IEndpoint Endpoint => _endpoint;
    private readonly IProcessor _processor;
    private readonly TcpEndpointOptions _options;
    private readonly Encoding _encoding;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoopTask;
    private long _processedCount;
    private readonly ConcurrentDictionary<string, TcpClient> _clients = new();
    private readonly ConcurrentDictionary<string, Task> _clientTasks = new();
    private readonly SemaphoreSlim? _connectionSemaphore;
    private readonly InflightDrainGuard _drain = new();

    private ILogger? _logger;

    /// <summary>Creates a TCP consumer.</summary>
    public TcpConsumer(TcpEndpoint endpoint, IProcessor processor, TcpEndpointOptions options)
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

    /// <summary>Number of currently connected clients.</summary>
    public int ActiveConnections => _clients.Count;

    /// <summary>The local endpoint the server is listening on. Available after Start().</summary>
    public IPEndPoint? LocalEndPoint => _listener?.LocalEndpoint as IPEndPoint;

    /// <inheritdoc />
    public Task Start(CancellationToken ct = default)
    {
        try
        {
            var ip = IPAddress.Parse(_options.Host);
            _listener = new TcpListener(ip, _options.Port);
            _listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _listener.Start(_options.Backlog);

            _cts = new CancellationTokenSource();
            _drain.Start(ct);
            _acceptLoopTask = AcceptLoopAsync(_cts.Token);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "TCP consumer Start failed on {Host}:{Port}", _options.Host, _options.Port);
            _listener?.Stop(); _listener = null;
            _cts?.Dispose(); _cts = null;
            _drain.Dispose();
            throw;
        }

        _logger ??= (_endpoint.Component as ComponentBase)?.Logger;
        _logger?.LogInformation("TCP consumer started: {Host}:{Port}", _options.Host, _options.Port);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task Stop(CancellationToken ct = default)
    {
        // Step 1: stop accepting new connections
        _cts?.Cancel();
        _listener?.Stop();

        if (_acceptLoopTask is not null)
        {
            try { await _acceptLoopTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }

        // Step 2: drain in-flight message processing
        await _drain.DrainAsync(ct, _logger, $"tcp:{_options.Host}:{_options.Port}").ConfigureAwait(false);

        // Step 3: disconnect clients to unblock read loops
        foreach (var (id, client) in _clients)
        {
            client.Dispose();
            _clients.TryRemove(id, out _);
        }

        // Step 4: await all client handler tasks
        if (!_clientTasks.IsEmpty)
        {
            try { await Task.WhenAll(_clientTasks.Values).ConfigureAwait(false); }
            catch { /* errors already logged per-client */ }
        }

        // Step 5: cleanup
        _listener = null;
        _cts?.Dispose();
        _cts = null;
        _drain.Dispose();
        _logger ??= (_endpoint.Component as ComponentBase)?.Logger;
        _logger?.LogInformation("TCP consumer stopped: {Host}:{Port}", _options.Host, _options.Port);
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener!.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) when (ct.IsCancellationRequested) { break; }

            ConfigureClient(client);
            var connectionId = Guid.NewGuid().ToString("N");
            _clients[connectionId] = client;

            // Handle each client in a tracked task
            var task = HandleClientAsync(client, connectionId, ct);
            _clientTasks[connectionId] = task;
            _ = task.ContinueWith(_ => _clientTasks.TryRemove(connectionId, out _), TaskContinuationOptions.ExecuteSynchronously);
        }
    }

    private async Task HandleClientAsync(TcpClient client, string connectionId, CancellationToken ct)
    {
        if (_connectionSemaphore is not null)
            await _connectionSemaphore.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            Stream stream = client.GetStream();

            if (_options.Ssl)
            {
                var sslStream = new SslStream(stream, leaveInnerStreamOpen: false);
#if NET9_0_OR_GREATER
                var cert = X509CertificateLoader.LoadPkcs12FromFile(_options.SslCertPath!, _options.SslCertPassword);
#else
                var cert = new X509Certificate2(_options.SslCertPath!, _options.SslCertPassword);
#endif
                await sslStream.AuthenticateAsServerAsync(cert).ConfigureAwait(false);
                stream = sslStream;
            }

            var remoteEp = client.Client.RemoteEndPoint?.ToString() ?? "unknown";
            var localEp = client.Client.LocalEndPoint?.ToString() ?? "unknown";

            while (!ct.IsCancellationRequested && client.Connected)
            {
                byte[]? data;
                try
                {
                    data = await TcpCodec.ReadMessageAsync(
                        stream, _options.Framing, _options.Delimiter, _options.ReceiveBufferSize, ct)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) { break; }
                catch (IOException) { break; }

                if (data is null) break; // Connection closed

                var exchange = BuildExchange(data, connectionId, remoteEp, localEp);

                _drain.Increment();
                try
                {
                    try
                    {
                        await _processor.Process(exchange, _drain.ProcessingToken).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogError(ex, "TCP message processing failed: remote={RemoteEndpoint}",
                            remoteEp);
                        exchange.Exception = ex;
                    }

                    // InOut: write response back (use drain-safe token so response completes during drain)
                    if (_options.InOut && exchange.Exception is null)
                    {
                        var responseData = ResolveResponseBody(exchange);
                        if (responseData is not null)
                        {
                            await TcpCodec.WriteMessageAsync(
                                stream, responseData, _options.Framing, _options.Delimiter, _encoding, _drain.ProcessingToken)
                                .ConfigureAwait(false);
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
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (SocketException) { }
        finally
        {
            _connectionSemaphore?.Release();
            client.Dispose();
            _clients.TryRemove(connectionId, out _);
        }
    }

    private IExchange BuildExchange(byte[] data, string connectionId, string remoteEp, string localEp)
    {
        // For TextLine, decode to string; otherwise keep as byte[]
        object body = _options.Framing == TcpFraming.TextLine
            ? _encoding.GetString(data)
            : data;

        var message = new Message(body);
        message.Headers[TcpHeaders.RemoteAddress] = remoteEp;
        message.Headers[TcpHeaders.LocalAddress] = localEp;
        message.Headers[TcpHeaders.ConnectionId] = connectionId;
        message.Headers[TcpHeaders.Framing] = _options.Framing.ToString();
        message.Headers[TcpHeaders.ByteCount] = data.Length.ToString();
        message.Headers[TcpHeaders.Ssl] = _options.Ssl.ToString();

        var exchange = Exchange.Create(message, _endpoint.ScopeFactory);
        exchange.Pattern = _options.InOut ? ExchangePattern.InOut : ExchangePattern.InOnly;

        return exchange;
    }

    private byte[]? ResolveResponseBody(IExchange exchange)
    {
        var outBody = exchange.Out?.Body;
        if (outBody is null) return null;

        return outBody switch
        {
            byte[] bytes => bytes,
            string str => _encoding.GetBytes(str),
            _ => _encoding.GetBytes(outBody.ToString()!)
        };
    }

    private void ConfigureClient(TcpClient client)
    {
        client.NoDelay = _options.NoDelay;
        client.ReceiveBufferSize = _options.ReceiveBufferSize;
        client.SendBufferSize = _options.SendBufferSize;
        client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, _options.KeepAlive);
    }
}

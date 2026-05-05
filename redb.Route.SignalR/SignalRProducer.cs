using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.Core;

namespace redb.Route.SignalR;

/// <summary>
/// SignalR producer. Two modes:
/// <list type="bullet">
/// <item><description><b>Client</b>: Uses <see cref="HubConnection"/> to connect to a remote SignalR hub and invoke methods.</description></item>
/// <item><description><b>Server</b>: Uses <see cref="IHubContext{THub}"/> to broadcast messages to clients of the local hub.</description></item>
/// </list>
/// </summary>
public class SignalRProducer : ConnectableProducer
{
    private readonly SignalREndpoint _endpoint;
    private readonly SignalREndpointOptions _options;
    private HubConnection? _hubConnection;
    private IHubContext<RedbBridgeHub>? _hubContext;

    /// <summary>Creates a SignalR producer.</summary>
    public SignalRProducer(SignalREndpoint endpoint, SignalREndpointOptions options)
    {
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <inheritdoc />
    protected override IEndpoint ProducerEndpoint => _endpoint;

    /// <inheritdoc />
    protected override string ProducerName => $"signalr:{_options.Mode}:{_endpoint.BuildClientUrl()}";

    /// <inheritdoc />
    protected override async Task ConnectAsync(CancellationToken ct)
    {
        if (_options.Mode == SignalRMode.Client)
            await StartClientMode(ct).ConfigureAwait(false);
        else
            StartServerMode();
    }

    /// <inheritdoc />
    protected override async Task DisconnectAsync(CancellationToken ct)
    {
        if (_hubConnection is not null)
        {
            try
            {
                await _hubConnection.StopAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logger?.LogDebug(ex, "SignalR: error stopping hub connection");
            }

            await _hubConnection.DisposeAsync().ConfigureAwait(false);
            _hubConnection = null;
        }

        _hubContext = null;
    }

    /// <inheritdoc />
    public override async Task Process(IExchange exchange, CancellationToken ct = default)
    {
        EnsureStarted();

        try
        {
            if (_options.Mode == SignalRMode.Client)
                await ProcessClientMode(exchange, ct).ConfigureAwait(false);
            else
                await ProcessServerMode(exchange, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger?.LogError(ex, "SignalR send failed: hub={HubPath}, method={Method}, mode={Mode}",
                _endpoint.HubPath, _options.Method, _options.Mode);
            throw;
        }
    }

    // ── Client mode ─────────────────────────────────────────────────

    private async Task StartClientMode(CancellationToken ct)
    {
        var url = _endpoint.BuildClientUrl();

        var builder = new HubConnectionBuilder()
            .WithUrl(url, connectionOptions =>
            {
                // Configure transport
                connectionOptions.Transports = _options.Transport switch
                {
                    SignalRTransport.ServerSentEvents => HttpTransportType.ServerSentEvents,
                    SignalRTransport.LongPolling => HttpTransportType.LongPolling,
                    _ => HttpTransportType.WebSockets
                };

                // Configure access token
                if (_options.AccessToken is not null)
                {
                    var token = _options.AccessToken;
                    connectionOptions.AccessTokenProvider = () => Task.FromResult<string?>(token);
                }

                // Skip HTTPS cert validation for dev scenarios when SSL is not set
                if (!_options.Ssl)
                {
                    connectionOptions.HttpMessageHandlerFactory = handler =>
                    {
                        if (handler is HttpClientHandler clientHandler)
                            clientHandler.ServerCertificateCustomValidationCallback =
                                HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
                        return handler;
                    };
                }
            });

        if (_options.Reconnect)
            builder.WithAutomaticReconnect(BuildRetryPolicy());

        _hubConnection = builder.Build();
        await _hubConnection.StartAsync(ct).ConfigureAwait(false);
    }

    private async Task ProcessClientMode(IExchange exchange, CancellationToken ct)
    {
        if (_hubConnection is null or { State: not HubConnectionState.Connected })
        {
            if (_options.Reconnect && _hubConnection is not null)
            {
                await _hubConnection.StartAsync(ct).ConfigureAwait(false);
            }
            else
            {
                throw new InvalidOperationException("Hub connection lost and reconnect is disabled.");
            }
        }

        var method = ResolveMethod(exchange);
        var body = exchange.In.Body;

        if (_options.Bridge)
        {
            // Bridge mode: route through RedbBridgeHub's single "Invoke(string method, object?[]? args)" entry point.
            // Used when connecting to our own hub (redb.Route route-to-route).
            var hubArgs = body switch
            {
                object?[] argsArray => argsArray,
                not null => new object?[] { body },
                _ => null
            };

            if (_options.InOut)
            {
                var result = await _hubConnection!.InvokeCoreAsync<object?>("Invoke", [method, hubArgs], ct).ConfigureAwait(false);
                var outMsg = new Message(result);
                outMsg.Headers[SignalRHeaders.Method] = method;
                exchange.Out = outMsg;
            }
            else
            {
                await _hubConnection!.SendCoreAsync("Invoke", [method, hubArgs], ct).ConfigureAwait(false);
            }
        }
        else
        {
            // Direct mode: call the hub method by its actual name.
            // Used when connecting to an external (third-party) SignalR hub.
            var directArgs = body switch
            {
                object?[] argsArray => argsArray,
                not null => [body],
                _ => Array.Empty<object?>()
            };

            if (_options.InOut)
            {
                var result = await _hubConnection!.InvokeCoreAsync<object?>(method, directArgs, ct).ConfigureAwait(false);
                var outMsg = new Message(result);
                outMsg.Headers[SignalRHeaders.Method] = method;
                exchange.Out = outMsg;
            }
            else
            {
                await _hubConnection!.SendCoreAsync(method, directArgs, ct).ConfigureAwait(false);
            }
        }
    }

    // ── Server mode ─────────────────────────────────────────────────

    private void StartServerMode()
    {
        // Find the consumer's WebApplication to get IHubContext
        // Server-mode producer needs the consumer running on the same endpoint
        var consumer = FindConsumer();
        if (consumer?.App is null)
            throw new InvalidOperationException(
                "Server-mode producer requires a running SignalR consumer on the same endpoint. " +
                "Ensure From(\"signalr:...\") is started before To(\"signalr:...?mode=server\").");

        _hubContext = consumer.App.Services.GetRequiredService<IHubContext<RedbBridgeHub>>();
    }

    private async Task ProcessServerMode(IExchange exchange, CancellationToken ct)
    {
        if (_hubContext is null)
            throw new InvalidOperationException("IHubContext not available. Is the consumer running?");

        var method = ResolveMethod(exchange);
        var body = exchange.In.Body;
        var args = body is object?[] argsArray ? argsArray : body is not null ? [body] : Array.Empty<object?>();

        var clients = ResolveTargetClients(exchange);
        await clients.SendCoreAsync(method, args, ct).ConfigureAwait(false);
    }

    private IClientProxy ResolveTargetClients(IExchange exchange)
    {
        var targetType = ResolveHeaderString(exchange, SignalRHeaders.Target)
                      ?? _options.ResolveOption(_options.TargetType, exchange) ?? _options.TargetType;
        var group = ResolveHeaderString(exchange, SignalRHeaders.Group)
                 ?? _options.ResolveOption(_options.TargetGroup, exchange);
        var connectionId = ResolveHeaderString(exchange, SignalRHeaders.TargetConnection);
        var userId = ResolveHeaderString(exchange, SignalRHeaders.TargetUser);

        return targetType.ToUpperInvariant() switch
        {
            "GROUP" when group is not null => _hubContext!.Clients.Group(group),
            "USER" when userId is not null => _hubContext!.Clients.User(userId),
            "CONNECTION" when connectionId is not null => _hubContext!.Clients.Client(connectionId),
            _ => _hubContext!.Clients.All
        };
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private string ResolveMethod(IExchange exchange)
    {
        if (exchange.In.Headers.TryGetValue(SignalRHeaders.Method, out var m) && m is string method
            && !string.IsNullOrEmpty(method))
            return method;

        if (_options.Method is not null)
            return _options.ResolveOption(_options.Method, exchange) ?? _options.Method;

        throw new InvalidOperationException(
            "No method specified. Set 'method' in URI or signalR.Method header on exchange.");
    }

    private static string? ResolveHeaderString(IExchange exchange, string header)
    {
        if (exchange.In.Headers.TryGetValue(header, out var val) && val is string s && !string.IsNullOrEmpty(s))
            return s;
        return null;
    }

    private SignalRConsumer? FindConsumer()
    {
        // The endpoint caches consumers/producers. Walk the route context to find the consumer
        // sharing our endpoint URI. For now, store on the endpoint itself.
        return _endpoint.Component is SignalRComponent comp
            ? comp.GetConsumer(_endpoint.Uri.NormalizedKey)
            : null;
    }

    private IRetryPolicy BuildRetryPolicy()
    {
        return new RedbRetryPolicy(_options.ReconnectInterval, _options.MaxReconnectAttempts);
    }

    /// <summary>Simple retry policy with configurable interval and max attempts.</summary>
    private sealed class RedbRetryPolicy : IRetryPolicy
    {
        private readonly int _intervalMs;
        private readonly int _maxAttempts;

        public RedbRetryPolicy(int intervalMs, int maxAttempts)
        {
            _intervalMs = intervalMs;
            _maxAttempts = maxAttempts;
        }

        public TimeSpan? NextRetryDelay(RetryContext retryContext)
        {
            if (_maxAttempts > 0 && retryContext.PreviousRetryCount >= _maxAttempts)
                return null;

            return TimeSpan.FromMilliseconds(_intervalMs);
        }
    }
}

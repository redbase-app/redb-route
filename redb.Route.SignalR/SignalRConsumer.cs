using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.Core;

namespace redb.Route.SignalR;

/// <summary>
/// SignalR consumer. Embedded Kestrel-based SignalR Hub server that accepts connections,
/// receives method invocations, and dispatches them into the route processor pipeline.
/// <para>Supports JSON and MessagePack protocols, group management, InOut exchange pattern,
/// and lifecycle events (Connected/Disconnected).</para>
/// </summary>
public class SignalRConsumer : IConsumer
{
    private readonly SignalREndpoint _endpoint;

    /// <inheritdoc />
    public IEndpoint Endpoint => _endpoint;

    private readonly IProcessor _processor;
    private readonly SignalREndpointOptions _options;
    private WebApplication? _app;
    private long _processedCount;
    private ILogger? _logger;

    /// <summary>Creates a SignalR consumer.</summary>
    public SignalRConsumer(SignalREndpoint endpoint, IProcessor processor, SignalREndpointOptions options)
    {
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        _processor = processor ?? throw new ArgumentNullException(nameof(processor));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = (endpoint.Component as ComponentBase)?.Logger;
    }

    /// <summary>Number of invocations successfully processed.</summary>
    public long ProcessedCount => Interlocked.Read(ref _processedCount);

    /// <summary>The base URL the server is listening on. Available after Start().</summary>
    public string? BaseUrl { get; private set; }

    /// <summary>The hub path for this consumer.</summary>
    public string HubPath => _endpoint.HubPath;

    /// <summary>The endpoint options, exposed for the bridge hub.</summary>
    internal SignalREndpointOptions EndpointOptions => _options;

    /// <summary>The service scope factory from the endpoint, for creating scoped exchanges.</summary>
    internal IServiceScopeFactory? ScopeFactory => _endpoint.ScopeFactory;

    /// <summary>The WebApplication instance. Exposed for server-mode producer to get IHubContext.</summary>
    internal WebApplication? App => _app;

    /// <summary>
    /// Processes an exchange through the pipeline. Called by <see cref="RedbBridgeHub"/>.
    /// </summary>
    internal async Task ProcessExchange(IExchange exchange, CancellationToken ct)
    {
        try
        {
            await _processor.Process(exchange, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "SignalR message processing failed: hub={HubPath}",
                _endpoint.HubPath);
            exchange.Exception ??= ex;
        }

        Interlocked.Increment(ref _processedCount);
    }

    /// <inheritdoc />
    public async Task Start(CancellationToken ct = default)
    {
        var builder = WebApplication.CreateSlimBuilder();

        // Register SignalR services
        var signalRBuilder = builder.Services.AddSignalR();

        if (_options.MessagePack)
            signalRBuilder.AddJsonProtocol(); // always add JSON as base

        // Register this consumer as singleton so RedbBridgeHub can access it
        builder.Services.AddSingleton(this);

        // Configure Kestrel
        builder.WebHost.ConfigureKestrel(ConfigureKestrel);
        builder.Logging.ClearProviders();

        _app = builder.Build();

        // Map the bridge hub
        var hubPath = _endpoint.HubPath;
        _app.MapHub<RedbBridgeHub>(hubPath, hubOptions =>
        {
            ConfigureHubOptions(hubOptions);
        });

        await _app.StartAsync(ct).ConfigureAwait(false);

        // Register with component for server-mode producer lookup
        if (_endpoint.Component is SignalRComponent comp)
            comp.RegisterConsumer(_endpoint.Uri.NormalizedKey, this);

        BaseUrl = BuildBaseUrl();
        _logger ??= (_endpoint.Component as ComponentBase)?.Logger;
        _logger?.LogInformation("SignalR consumer started: {Url}{Path}", BaseUrl, hubPath);
    }

    /// <inheritdoc />
    public async Task Stop(CancellationToken ct = default)
    {
        // Unregister from component
        if (_endpoint.Component is SignalRComponent comp)
            comp.UnregisterConsumer(_endpoint.Uri.NormalizedKey);

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

        _logger ??= (_endpoint.Component as ComponentBase)?.Logger;
        _logger?.LogInformation("SignalR consumer stopped");
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

    private void ConfigureHubOptions(HttpConnectionDispatcherOptions options)
    {
        // Configure allowed transports
        options.Transports = _options.Transport switch
        {
            SignalRTransport.ServerSentEvents => HttpTransportType.ServerSentEvents,
            SignalRTransport.LongPolling => HttpTransportType.LongPolling,
            _ => HttpTransportType.WebSockets | HttpTransportType.ServerSentEvents | HttpTransportType.LongPolling
        };
    }

    private string BuildBaseUrl()
    {
        var scheme = _options.Ssl ? "https" : "http";
        var host = _options.Host == "0.0.0.0" ? "127.0.0.1" : _options.Host;
        return $"{scheme}://{host}:{_options.Port}";
    }
}

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
using redb.Route.Http;

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
    private SharedHttpServerManager? _serverManager;
    private readonly InflightDrainGuard _drain = new();
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

    // ── Connection admission (HTTP_CONCURRENCY_LIMITS_PLAN, волна В4) ──

    private int _connections;

    /// <summary>
    /// Claims a connection slot against <c>maxConnections</c>. A refusal is counted in the
    /// endpoint's Rejected; the hub aborts the connection before the Connected lifecycle event
    /// can reach the pipeline. Always 'true' when the limit is off (0).
    /// </summary>
    internal bool TryAcquireConnection()
    {
        if (_options.MaxConnections <= 0) return true;

        var now = Interlocked.Increment(ref _connections);
        if (now <= _options.MaxConnections) return true;

        Interlocked.Decrement(ref _connections);
        _endpoint.RecordRejected();
        _logger?.LogWarning(
            "SignalR hub {HubPath}: connection refused - maxConnections={Max} reached",
            HubPath, _options.MaxConnections);
        return false;
    }

    /// <summary>Releases a slot claimed by <see cref="TryAcquireConnection"/>.</summary>
    internal void ReleaseConnection()
    {
        if (_options.MaxConnections > 0)
            Interlocked.Decrement(ref _connections);
    }

    /// <summary>The base URL the server is listening on. Available after Start().</summary>
    public string? BaseUrl { get; private set; }

    /// <summary>The hub path for this consumer.</summary>
    public string HubPath => _endpoint.HubPath;

    /// <summary>The endpoint options, exposed for the bridge hub.</summary>
    internal SignalREndpointOptions EndpointOptions => _options;

    /// <summary>The service scope factory from the endpoint, for creating scoped exchanges.</summary>
    internal IServiceScopeFactory? ScopeFactory => _endpoint.ScopeFactory;

    /// <summary>
    /// Services of the listener this hub is mapped on. The server-mode producer resolves
    /// <c>IHubContext</c> from here; it is captured when the shared listener is built.
    /// </summary>
    internal IServiceProvider? HubServices =>
        (_endpoint.Component as SignalRComponent)?.GetHubServices(_options.Host, _options.Port);

    /// <summary>
    /// Processes an exchange through the pipeline. Called by <see cref="RedbBridgeHub"/>.
    /// </summary>
    internal async Task ProcessExchange(IExchange exchange, CancellationToken ct)
    {
        // Pipeline statistics belong to the core: StatisticsProcessor wraps a routed consumer
        // and counts MessagesIn/Errors - self-recording here double-counted them (ownership audit).
        _drain.Increment();
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
        finally
        {
            _drain.Decrement();
        }

        Interlocked.Increment(ref _processedCount);
    }

    /// <inheritdoc />
    public async Task Start(CancellationToken ct = default)
    {
        // ssl=true without a certificate anywhere refuses to bind — enforced once, by the shared
        // host, which is the only place that sees the endpoint, the connection factory and the
        // host default together.
        _drain.Start(ct);

        // The hub itself was registered with the listener during the endpoint's Start (SignalR
        // services must exist before Build(), MapHub before the catch-all). Here we only make sure
        // the listener is running — it may already be serving HTTP, gRPC, SOAP or another hub.
        _serverManager = _endpoint.ServerManager;

        // Idempotent: the endpoint already did this during its own Start when the route context
        // owns the lifecycle. Repeating it keeps a hand-built consumer working.
        _endpoint.EnsureHubRegistered();

        // Registered before the listener starts: a client can connect the moment it is up.
        if (_endpoint.Component is SignalRComponent comp)
            comp.RegisterConsumer(this);

        try
        {
            await _serverManager.EnsureStarted(_options.Host, _options.Port, ct).ConfigureAwait(false);
        }
        catch
        {
            if (_endpoint.Component is SignalRComponent failed)
                failed.UnregisterConsumer(this);
            _endpoint.ReleaseHubRegistration();
            _drain.Dispose();
            throw;
        }

        BaseUrl = _serverManager.GetBaseUrl(_options.Host, _options.Port) ?? BuildBaseUrl();
        _logger ??= (_endpoint.Component as ComponentBase)?.Logger;
        _logger?.LogInformation("SignalR consumer started: {Url}{Path}", BaseUrl, _endpoint.HubPath);
    }

    /// <inheritdoc />
    public async Task Stop(CancellationToken ct = default)
    {
        // Stop accepting new invocations: the hub can no longer find this consumer.
        if (_endpoint.Component is SignalRComponent comp)
            comp.UnregisterConsumer(this);

        // Drain invocations already inside the pipeline before the listener can go away,
        // so a deploy does not cut them mid-flight.
        await _drain.DrainAsync(ct, _logger, $"signalr://{_endpoint.HubPath}").ConfigureAwait(false);

        // Give the hub's place on the listener back before asking it to stop: until then the
        // listener has an occupant, and a later Start must register the hub again.
        _endpoint.ReleaseHubRegistration();

        // The listener stops only when the last route AND the last hub on it are gone: HTTP or
        // another hub may still be serving this port.
        if (_serverManager is not null)
        {
            await _serverManager.StopIfEmpty(_options.Host, _options.Port, ct).ConfigureAwait(false);
            _serverManager = null;
        }

        _drain.Dispose();
        _logger ??= (_endpoint.Component as ComponentBase)?.Logger;
        _logger?.LogInformation("SignalR consumer stopped");
    }

    private string BuildBaseUrl()
    {
        var scheme = _options.Ssl ? "https" : "http";
        var host = _options.Host == "0.0.0.0" ? "127.0.0.1" : _options.Host;
        return $"{scheme}://{host}:{_options.Port}";
    }
}

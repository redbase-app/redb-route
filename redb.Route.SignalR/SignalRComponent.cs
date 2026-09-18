using System.Collections.Concurrent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using System.Security.Claims;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.Extensions;
using redb.Route.Core;
using redb.Route.Http;

namespace redb.Route.SignalR;

/// <summary>
/// SignalR component. Scheme: "signalr".
/// <para>Consumer: Kestrel-based SignalR Hub server — accepts connections and receives method invocations.</para>
/// <para>Producer (client): HubConnection — connects to a remote SignalR hub and invokes methods.</para>
/// <para>Producer (server): IHubContext — broadcasts to clients of the local hub.</para>
/// <para>URI format: signalr:host:port/hubPath?method=Send&amp;inOut=true&amp;messagePack=true</para>
/// </summary>
public class SignalRComponent : ComponentBase
{
    private readonly ConcurrentDictionary<string, SignalRConsumer> _consumers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SignalRConsumer> _consumersByHubPath = new(StringComparer.OrdinalIgnoreCase);
    private SharedHttpServerManager? _ownedServerManager;

    /// <inheritdoc />
    public override string Scheme => "signalr";

    /// <summary>
    /// Shared Kestrel host. Set by <c>AddRedbRouteSignalR()</c> so a hub multiplexes onto the same
    /// listener as HTTP, gRPC, SOAP and AS2 — that is what lets <c>/hub/</c> and the REST API live
    /// on one port behind one reverse proxy. A hand-built component owns a private manager instead
    /// of failing, so <c>new SignalRComponent()</c> stays usable on its own.
    /// </summary>
    public SharedHttpServerManager? ServerManager { get; set; }

    /// <summary>The manager actually used: the injected one, or a private one created on demand.</summary>
    internal SharedHttpServerManager EffectiveServerManager
    {
        get
        {
            if (ServerManager is not null) return ServerManager;
            return _ownedServerManager ??= new SharedHttpServerManager();
        }
    }

    /// <summary>
    /// Extra service registrations for the hub's listener, supplied by the host: a SignalR
    /// backplane for scale-out (<c>AddStackExchangeRedis</c>, Azure SignalR), extra protocols,
    /// anything else the hub needs. Kept as a delegate on purpose — the connector stays free of
    /// those dependencies and the host picks its own.
    /// </summary>
    public Action<IServiceCollection>? HubServicesConfigurator { get; set; }

    /// <summary>
    /// Authenticates hub connections. Without it the hub accepts anonymous connections and
    /// <c>UserIdentifier</c> stays empty, which makes <c>Clients.User(...)</c> useless. Supplied by
    /// the host (see <c>AddRedbRouteSignalR(o =&gt; o.Authenticate = ...)</c>) rather than built in,
    /// because token validation belongs to whoever issues the tokens. The principal is put on every
    /// exchange the hub produces (<c>ExchangePrincipal</c>); build its identity with an authentication
    /// type, because code that reads it treats an identity that is not authenticated as anonymous.
    /// </summary>
    public Func<HttpContext, Task<ClaimsPrincipal?>>? Authenticate { get; set; }

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        if (_ownedServerManager is not null)
        {
            await _ownedServerManager.DisposeAsync().ConfigureAwait(false);
            _ownedServerManager = null;
        }
        await base.DisposeAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override IEndpoint CreateEndpoint(EndpointUri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);

        var options = new SignalREndpointOptions();
        ParseHostPort(uri.Path, options);
        options.BindFromUri(uri.RawParameters);

        // Named ConnectionFactory keeps the access token / cert password out of the route URI.
        if (!string.IsNullOrEmpty(options.ConnectionFactory))
        {
            // A set-but-unknown name fails loud -- never a silent fallback to URI params (Ф11 Ж-1).
            var factory = Context.GetRequiredFromRegistry<SignalRConnectionFactory>(options.ConnectionFactory);
            factory.ApplyTo(options, uri);
        }

        options.Validate();

        return new SignalREndpoint(uri, this, options);
    }

    /// <summary>
    /// Registers a consumer for server-mode producer lookup and for hub dispatch.
    /// <para>
    /// The lookup key is the listener address plus the hub path, NOT the endpoint's normalized
    /// key: that one carries the sorted query parameters, and a server-mode producer always
    /// carries at least <c>mode=server</c>, so it never matched the consumer it was meant to push
    /// through.
    /// </para>
    /// </summary>
    internal void RegisterConsumer(SignalRConsumer consumer)
    {
        _consumers[ConsumerKey(consumer.EndpointOptions.Host, consumer.EndpointOptions.Port, consumer.HubPath)] = consumer;
        _consumersByHubPath[DispatchKey(consumer.EndpointOptions.Port, consumer.HubPath)] = consumer;
    }

    /// <summary>Unregisters a consumer.</summary>
    internal void UnregisterConsumer(SignalRConsumer consumer)
    {
        _consumers.TryRemove(ConsumerKey(consumer.EndpointOptions.Host, consumer.EndpointOptions.Port, consumer.HubPath), out _);
        _consumersByHubPath.TryRemove(DispatchKey(consumer.EndpointOptions.Port, consumer.HubPath), out _);
    }

    internal static string ConsumerKey(string host, int port, string hubPath) => $"{host}:{port}{hubPath}";

    // Dispatch is per listener, so the port is part of the key: two hubs sharing a path on
    // different ports are different hubs, and keying on the path alone let the later one answer
    // for both.
    private static string DispatchKey(int port, string hubPath) => $"{port}{hubPath}";

    /// <summary>
    /// Finds the consumer serving a hub request. Several hubs can share one listener now, so the
    /// bridge hub resolves its consumer by the request path (<c>/chatHub</c>, <c>/chatHub/negotiate</c>)
    /// rather than by being the only one in the container. Longest prefix wins.
    /// </summary>
    internal SignalRConsumer? GetConsumerByRequestPath(int localPort, string? requestPath)
    {
        if (string.IsNullOrEmpty(requestPath)) return null;

        var prefix = localPort.ToString();
        if (_consumersByHubPath.TryGetValue(DispatchKey(localPort, requestPath), out var exact))
            return exact;

        SignalRConsumer? best = null;
        var bestLength = -1;
        foreach (var (key, consumer) in _consumersByHubPath)
        {
            if (!key.StartsWith(prefix, StringComparison.Ordinal)) continue;

            var hubPath = key[prefix.Length..];
            if (hubPath.Length > bestLength
                && requestPath.StartsWith(hubPath, StringComparison.OrdinalIgnoreCase))
            {
                best = consumer;
                bestLength = hubPath.Length;
            }
        }
        return best;
    }

    /// <summary>Gets the running consumer serving a hub, by listener address and hub path.</summary>
    internal SignalRConsumer? GetConsumer(string host, int port, string hubPath)
        => _consumers.TryGetValue(ConsumerKey(host, port, hubPath), out var c) ? c : null;

    private readonly ConcurrentDictionary<string, IServiceProvider> _hubServices = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Captures the listener's services when the shared host builds it.</summary>
    internal void RegisterHubServices(string host, int port, IServiceProvider services)
        => _hubServices[$"{host}:{port}"] = services;

    /// <summary>Services of the listener serving hubs on this host:port, if it has been built.</summary>
    internal IServiceProvider? GetHubServices(string host, int port)
        => _hubServices.TryGetValue($"{host}:{port}", out var sp) ? sp : null;

    /// <summary>
    /// Parses "host:port/hubPath" from the URI path segment.
    /// Sets Host and Port on options. HubPath is extracted separately via <see cref="ExtractHubPath"/>.
    /// </summary>
    internal static void ParseHostPort(string path, SignalREndpointOptions options)
    {
        var clean = path.TrimStart('/');
        var slashIdx = clean.IndexOf('/');
        var hostPort = slashIdx >= 0 ? clean[..slashIdx] : clean;

        var colonIdx = hostPort.LastIndexOf(':');
        if (colonIdx > 0 && int.TryParse(hostPort[(colonIdx + 1)..], out var port))
        {
            options.Host = hostPort[..colonIdx];
            options.Port = port;
        }
        else
        {
            options.Host = hostPort;
        }
    }

    /// <summary>
    /// Extracts the hub path after host:port.
    /// "0.0.0.0:5000/chatHub" → "/chatHub"
    /// "0.0.0.0:5000" → "/"
    /// </summary>
    internal static string ExtractHubPath(string uriPath)
    {
        var clean = uriPath.TrimStart('/');
        var slashIdx = clean.IndexOf('/');
        return slashIdx >= 0 ? clean[slashIdx..] : "/";
    }
}

/// <summary>
/// SignalR endpoint. Creates either a producer or consumer for the SignalR hub.
/// </summary>
public class SignalREndpoint : EndpointBase<SignalREndpointOptions>
{
    /// <summary>Creates a SignalR endpoint.</summary>
    public SignalREndpoint(EndpointUri uri, SignalRComponent component, SignalREndpointOptions options)
        : base(uri, component, options)
    {
    }

    /// <summary>The hub path (after host:port in the URI).</summary>
    public string HubPath => SignalRComponent.ExtractHubPath(Uri.Path);

    /// <summary>The shared host this endpoint serves on.</summary>
    internal SharedHttpServerManager ServerManager => ((SignalRComponent)Component).EffectiveServerManager;

    /// <summary>
    /// Registers the hub with the shared listener BEFORE any consumer starts it: SignalR services
    /// have to be in the container before <c>Build()</c>, and <c>MapHub</c> before the catch-all,
    /// neither of which can be done to a running server. The route context starts every endpoint
    /// before it starts consumers, and only the consumer calls EnsureStarted, so this ordering
    /// holds without any extra coordination.
    /// </summary>
    public override Task Start(CancellationToken ct = default)
    {
        EnsureHubRegistered();
        return Task.CompletedTask;
    }

    private readonly object _hubRegistrationLock = new();
    private ServerConfiguratorRegistration? _hubRegistration;

    /// <summary>
    /// Registers the hub with the listener exactly once per run. Called from <see cref="Start"/>
    /// when the route context owns the lifecycle, and again from the consumer so a hand-built
    /// consumer (tests, embedded use) works without anyone having started the endpoint first.
    /// </summary>
    internal void EnsureHubRegistered()
    {
        lock (_hubRegistrationLock)
        {
            if (_hubRegistration is not null) return;
            _hubRegistration = BuildHubRegistration();
        }
    }

    /// <summary>
    /// Gives the listener's configurator back when the consumer stops, so the listener can shut
    /// down once nothing occupies it — and so a later Start registers the hub again instead of
    /// finding a listener that no longer exists.
    /// </summary>
    internal void ReleaseHubRegistration()
    {
        lock (_hubRegistrationLock)
        {
            if (_hubRegistration is null) return;
            ((SignalRComponent)Component).EffectiveServerManager.UnregisterServerConfigurator(_hubRegistration);
            _hubRegistration = null;
        }
    }

    private ServerConfiguratorRegistration BuildHubRegistration()
    {
        var component = (SignalRComponent)Component;
        var hubPath = HubPath;

        return component.EffectiveServerManager.RegisterServerConfigurator(
            Options.Host, Options.Port,
            services =>
            {
                var signalR = services.AddSignalR(hub =>
                {
                    // Волна В4 плана лимитов: SignalR's own per-client invocation parallelism.
                    // 0 keeps SignalR's default (1 = per-client serial).
                    if (Options.MaxParallelInvocationsPerClient > 0)
                        hub.MaximumParallelInvocationsPerClient = Options.MaxParallelInvocationsPerClient;
                });
                if (Options.MessagePack)
                    signalR.AddMessagePackProtocol();

                // The bridge hub resolves its consumer through the component.
                services.TryAddSingleton(component);

                // Host-supplied extras: backplane for scale-out, custom protocols, and so on.
                component.HubServicesConfigurator?.Invoke(services);
            },
            app =>
            {
                // Authentication gate in front of the hub path: the host's own validator decides,
                // and a rejected handshake gets 401 before the connection is upgraded. Placed here
                // (not as ASP.NET authentication) because the process hosting the route context is
                // a generic host, not an ASP.NET application. Without the component's own delegate,
                // the caller is whoever the shared host's resolver identified (it runs earlier in the
                // pipeline), and that principal is handed to SignalR the same way.
                var authenticate = component.Authenticate;
                if (authenticate is not null || component.EffectiveServerManager.Options.ResolvePrincipal is not null)
                {
                    app.Use(async (ctx, next) =>
                    {
                        if (!ctx.Request.Path.StartsWithSegments(hubPath))
                        {
                            await next().ConfigureAwait(false);
                            return;
                        }

                        if (authenticate is not null)
                        {
                            var principal = await authenticate(ctx).ConfigureAwait(false);
                            if (principal is null)
                            {
                                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                                return;
                            }

                            // SignalR reads UserIdentifier from HttpContext.User.
                            ctx.User = principal;
                        }
                        else if (SharedHttpServerManager.GetResolvedPrincipal(ctx) is { } resolved)
                        {
                            ctx.User = resolved;
                        }

                        await next().ConfigureAwait(false);
                    });
                }

                app.MapHub<RedbBridgeHub>(hubPath, ConfigureHubOptions);
                // The server-mode producer resolves IHubContext from these services.
                component.RegisterHubServices(Options.Host, Options.Port, app.Services);
            },
            // A hub maps itself instead of calling RegisterRoute, so this is where the listener
            // learns it has to terminate TLS.
            Options.Ssl, Options.SslCertPath, Options.SslCertPassword);
    }

    private void ConfigureHubOptions(HttpConnectionDispatcherOptions options)
    {
        options.Transports = Options.Transport switch
        {
            SignalRTransport.ServerSentEvents => HttpTransportType.ServerSentEvents,
            SignalRTransport.LongPolling => HttpTransportType.LongPolling,
            _ => HttpTransportType.WebSockets | HttpTransportType.ServerSentEvents | HttpTransportType.LongPolling
        };
    }

    /// <summary>The endpoint options for external access.</summary>
    internal SignalREndpointOptions EndpointOptions => Options;

    /// <summary>
    /// Builds the full URL for producer client connections.
    /// "signalr:api.example.com:5000/chatHub" → "http://api.example.com:5000/chatHub"
    /// </summary>
    public string BuildClientUrl()
    {
        var scheme = Options.Ssl ? "https" : "http";
        var host = Options.Host;
        var hubPath = HubPath;
        return $"{scheme}://{host}:{Options.Port}{hubPath}";
    }

    /// <inheritdoc />
    public override IProducer CreateProducer()
    {
        return new SignalRProducer(this, Options);
    }

    /// <inheritdoc />
    public override IConsumer CreateConsumer(IProcessor processor)
    {
        ArgumentNullException.ThrowIfNull(processor);
        return new SignalRConsumer(this, processor, Options);
    }
}

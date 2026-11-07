using System.Collections.Concurrent;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using System.Security.Claims;
using Microsoft.Extensions.DependencyInjection;
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

    /// <summary>
    /// The manager actually used. Resolution order: explicitly assigned → the context's DI singleton
    /// → a private instance. The DI step matters for module hosts that add the component by scanning
    /// rather than through the extension method, so nothing ever assigns <see cref="ServerManager"/>
    /// while the one shared manager sits in the container (gRPC and AS2 resolve the same way).
    /// </summary>
    internal SharedHttpServerManager EffectiveServerManager =>
        ServerManager
        ?? Context.Resolve<SharedHttpServerManager>()
        ?? (_ownedServerManager ??= new SharedHttpServerManager());

    /// <summary>
    /// Extra service registrations for the container of each hub, supplied by the host: a SignalR
    /// backplane for scale-out, extra protocols, anything else the hub needs. Runs once per hub, with
    /// the hub it is for. Kept as a delegate on purpose: the connector stays free of those
    /// dependencies and the host picks its own.
    /// </summary>
    public Action<IServiceCollection, SignalRHubDescriptor>? HubServicesConfigurator { get; set; }

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
    /// Registers a running consumer for the bridge hub and for server-mode producer lookup.
    /// <para>
    /// The lookup key is the listener address plus the hub path, NOT the endpoint's normalized
    /// key: that one carries the sorted query parameters, and a server-mode producer always
    /// carries at least <c>mode=server</c>, so it never matched the consumer it was meant to push
    /// through.
    /// </para>
    /// </summary>
    internal void RegisterConsumer(SignalRConsumer consumer)
        => _consumers[ConsumerKey(consumer.EndpointOptions.Host, consumer.EndpointOptions.Port, consumer.HubPath)] = consumer;

    /// <summary>Unregisters a consumer.</summary>
    internal void UnregisterConsumer(SignalRConsumer consumer)
        => _consumers.TryRemove(ConsumerKey(consumer.EndpointOptions.Host, consumer.EndpointOptions.Port, consumer.HubPath), out _);

    internal static string ConsumerKey(string host, int port, string hubPath) => $"{host}:{port}{hubPath}";

    /// <summary>Gets the running consumer serving a hub, by listener address and hub path.</summary>
    internal SignalRConsumer? GetConsumer(string host, int port, string hubPath)
        => _consumers.TryGetValue(ConsumerKey(host, port, hubPath), out var c) ? c : null;

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
    /// Registers the hub with the shared listener, when this endpoint serves one: a consumer was created on it (the
    /// route context creates every consumer while it compiles the routes, before it starts any endpoint). A producer
    /// endpoint (<c>mode=server</c> broadcast, <c>mode=client</c> connection) maps nothing: the server-mode producer
    /// sends through the hub its consumer registered, and a second registration of one hub path would map it twice.
    /// </summary>
    public override async Task Start(CancellationToken ct = default)
    {
        if (Volatile.Read(ref _servesHub))
            await EnsureHubRegisteredAsync(ct).ConfigureAwait(false);
    }

    // Set by CreateConsumer: this endpoint is a hub, not a producer that talks to one.
    private bool _servesHub;

    // Serializes putting the hub on the listener and taking it off; _hub itself is read without it.
    private readonly SemaphoreSlim _hubGate = new(1, 1);
    private SignalRHubHost? _hub;

    /// <summary>
    /// The hub's own container while the hub is on the listener: the server-mode producer resolves
    /// <c>IHubContext</c> from here.
    /// </summary>
    internal IServiceProvider? HubServices
    {
        get { return Volatile.Read(ref _hub)?.Services; }
    }

    /// <summary>The consumer this hub dispatches to, while it runs.</summary>
    internal SignalRConsumer? ServingConsumer
        => ((SignalRComponent)Component).GetConsumer(Options.Host, Options.Port, HubPath);

    /// <summary>
    /// Puts the hub on the listener exactly once per run, with a container of its own (see
    /// <see cref="SignalRHubHost"/>), whether the listener is already serving or not. Called from
    /// <see cref="Start"/> when the route context owns the lifecycle, and again from the consumer so a
    /// hand-built consumer (tests, embedded use) works without anyone having started the endpoint first.
    /// </summary>
    internal async Task EnsureHubRegisteredAsync(CancellationToken ct)
    {
        await _hubGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_hub is null)
                Volatile.Write(ref _hub, await SignalRHubHost.StartAsync(this, ct).ConfigureAwait(false));
        }
        finally
        {
            _hubGate.Release();
        }
    }

    /// <summary>
    /// Takes the hub off the listener when the consumer stops: its endpoints go, its connections are
    /// closed and its container is disposed, while the other hubs and routes on the listener keep
    /// serving. The listener can then shut down once nothing occupies it, and a later Start puts the
    /// hub back instead of finding a listener that no longer exists.
    /// </summary>
    internal async Task ReleaseHubRegistrationAsync()
    {
        await _hubGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var hub = Interlocked.Exchange(ref _hub, null);
            if (hub is not null)
                await hub.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _hubGate.Release();
        }
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
        Volatile.Write(ref _servesHub, true);
        return new SignalRConsumer(this, processor, Options);
    }
}

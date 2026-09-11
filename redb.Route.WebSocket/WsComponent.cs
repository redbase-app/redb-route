using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.Extensions;
using redb.Route.Core;
using redb.Route.Http;

namespace redb.Route.WebSocket;

/// <summary>
/// WebSocket component. Scheme: "ws" (plain) or "wss" (TLS).
/// <para>Producer: ClientWebSocket — connects to remote WebSocket server and sends frames.</para>
/// <para>Consumer: Kestrel-based WebSocket server — accepts connections and receives frames.</para>
/// <para>URI format: ws:host:port/path?messageType=Text&amp;subProtocol=graphql-ws</para>
/// </summary>
public class WsComponent : ComponentBase
{
    private readonly string _scheme;
    private SharedHttpServerManager? _ownedServerManager;

    /// <summary>Creates a WebSocket component with the given scheme.</summary>
    public WsComponent(string scheme = "ws")
    {
        _scheme = scheme;
    }

    /// <inheritdoc />
    public override string Scheme => _scheme;

    /// <summary>
    /// Shared Kestrel host. Set by <c>AddRedbRouteWebSocket()</c> so ws endpoints multiplex onto
    /// the same listener as HTTP, gRPC, SOAP and AS2 (and inherit its trusted-proxy, CORS and TLS
    /// handling). When the component is constructed by hand — tests, embedded scenarios — it owns
    /// a private manager instead of failing, which keeps `new WsComponent()` usable on its own.
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
    /// Authenticates a handshake before the socket is upgraded. Supplied by the host through
    /// <c>AddRedbRouteWebSocket(o =&gt; o.Authenticate = ...)</c>; null means every connection is
    /// accepted, which is the historical behaviour.
    /// </summary>
    public Func<Microsoft.AspNetCore.Http.HttpContext, Task<System.Security.Claims.ClaimsPrincipal?>>? Authenticate { get; set; }

    // Running consumers, so a server-mode producer can push into the clients of the consumer
    // serving the same address. The key is host:port plus path, NOT the endpoint's normalized key:
    // that one carries the sorted query parameters, and a producer always differs from its
    // consumer by at least mode=Server.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, WsConsumer> _consumers =
        new(StringComparer.OrdinalIgnoreCase);

    internal static string ConsumerKey(string host, int port, string path) => $"{host}:{port}{path}";

    internal void RegisterConsumer(WsConsumer consumer) =>
        _consumers[ConsumerKey(consumer.EndpointOptions.Host, consumer.EndpointOptions.Port, consumer.ConsumerPath)] = consumer;

    internal void UnregisterConsumer(WsConsumer consumer) =>
        _consumers.TryRemove(ConsumerKey(consumer.EndpointOptions.Host, consumer.EndpointOptions.Port, consumer.ConsumerPath), out _);

    internal WsConsumer? GetConsumer(string host, int port, string path) =>
        _consumers.GetValueOrDefault(ConsumerKey(host, port, path));

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

        var options = new WsEndpointOptions();
        ParseHostPort(uri.Path, options);
        options.BindFromUri(uri.RawParameters);

        // Named ConnectionFactory keeps the TLS certificate password out of the route URI.
        // Applied before the wss override so the scheme still wins on TLS.
        if (!string.IsNullOrEmpty(options.ConnectionFactory))
        {
            // A set-but-unknown name fails loud -- never a silent fallback to URI params (Ф11 Ж-1).
            var factory = Context.GetRequiredFromRegistry<WsConnectionFactory>(options.ConnectionFactory);
            factory.ApplyTo(options, uri);
        }

        // wss implies SSL
        if (_scheme == "wss")
            options.Ssl = true;

        options.Validate();

        return new WsEndpoint(uri, this, options);
    }

    /// <summary>
    /// Parses "host:port/path" from the URI path segment.
    /// Path format: "/host:port/route" or "host:port" or "/host:port".
    /// </summary>
    internal static void ParseHostPort(string path, WsEndpointOptions options)
    {
        var clean = path.TrimStart('/');
        // Find host:port — stop at the first slash after host:port
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
    /// Extracts the path portion after host:port (for consumer route matching).
    /// "/0.0.0.0:9000/chat" → "/chat"
    /// "/0.0.0.0:9000" → "/"
    /// </summary>
    internal static string ExtractPath(string uriPath)
    {
        var clean = uriPath.TrimStart('/');
        var slashIdx = clean.IndexOf('/');
        return slashIdx >= 0 ? clean[slashIdx..] : "/";
    }
}

/// <summary>
/// WSS (WebSocket Secure) component. Uses the "wss" scheme and enables TLS.
/// </summary>
public class WssComponent : WsComponent
{
    /// <summary>Creates a WSS component.</summary>
    public WssComponent() : base("wss") { }
}

/// <summary>
/// WebSocket endpoint. Creates either a producer (ClientWebSocket) or consumer (Kestrel WS server).
/// </summary>
public class WsEndpoint : EndpointBase<WsEndpointOptions>
{
    /// <summary>Creates a WebSocket endpoint.</summary>
    public WsEndpoint(EndpointUri uri, WsComponent component, WsEndpointOptions options)
        : base(uri, component, options)
    {
    }

    /// <summary>The consumer route path (after host:port).</summary>
    public string ConsumerPath => WsComponent.ExtractPath(Uri.Path);

    /// <summary>The shared host this endpoint serves on.</summary>
    internal SharedHttpServerManager ServerManager => ((WsComponent)Component).EffectiveServerManager;

    /// <summary>
    /// Declares WebSocket support on the listener BEFORE any consumer starts it. The route context
    /// starts every endpoint before it starts consumers, and only the consumer calls EnsureStarted,
    /// so by the time the listener is built the middleware is guaranteed to be requested.
    /// </summary>
    public override Task Start(CancellationToken ct = default)
    {
        ServerManager.EnableWebSockets(Options.Host, Options.Port,
            Options.KeepAliveInterval > 0
                ? TimeSpan.FromMilliseconds(Options.KeepAliveInterval)
                : null,
            Options.Ssl, Options.SslCertPath, Options.SslCertPassword);
        return Task.CompletedTask;
    }

    /// <summary>The endpoint options for external access.</summary>
    internal WsEndpointOptions EndpointOptions => Options;

    /// <summary>
    /// Builds the full WebSocket URL for the producer client.
    /// "ws:echo.example.com:8080/feed" → "ws://echo.example.com:8080/feed"
    /// </summary>
    public string BuildProducerUrl()
    {
        var scheme = Options.Ssl ? "wss" : "ws";
        var path = Uri.Path.TrimStart('/');
        return $"{scheme}://{path}";
    }

    /// <inheritdoc />
    public override IProducer CreateProducer()
    {
        return new WsProducer(this, Options);
    }

    /// <inheritdoc />
    public override IConsumer CreateConsumer(IProcessor processor)
    {
        ArgumentNullException.ThrowIfNull(processor);
        return new WsConsumer(this, processor, Options);
    }
}

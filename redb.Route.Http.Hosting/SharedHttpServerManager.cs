using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Claims;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Template;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace redb.Route.Http;

/// <summary>
/// Manages shared Kestrel server instances across multiple route contexts.
/// One server per (host, port) combination. Multiple consumers can register routes
/// on the same port. Supports dynamic route addition/removal at runtime.
/// </summary>
public sealed class SharedHttpServerManager : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, ServerEntry> _servers = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();
    private readonly HttpHostingOptions _options;
    private readonly ILogger? _logger;

    /// <summary>Key under which the pre-resolution socket address is kept in <c>HttpContext.Items</c>.</summary>
    public const string OriginalRemoteAddressItem = "redb.OriginalRemoteAddress";

    /// <summary>Key under which the pre-resolution scheme is kept in <c>HttpContext.Items</c>.</summary>
    public const string OriginalSchemeItem = "redb.OriginalScheme";

    /// <summary>
    /// Key under which the caller's principal, as produced by <see cref="HttpHostingOptions.ResolvePrincipal"/>,
    /// is kept in <c>HttpContext.Items</c>. Absent when no resolver is configured or it returned null.
    /// </summary>
    public const string PrincipalItem = "redb.Principal";

    /// <summary>Creates a manager with default options: no trusted proxies, forwarded headers ignored.</summary>
    public SharedHttpServerManager() : this(null)
    {
    }

    /// <summary>
    /// Creates a manager with explicit host options. Under DI the options arrive through
    /// <c>AddRedbRouteHttpHosting(configure)</c>; a host that constructs the manager by hand
    /// passes them here.
    /// </summary>
    public SharedHttpServerManager(HttpHostingOptions? options) : this(options, null)
    {
    }

    /// <summary>
    /// Creates a manager with host options and a logger for the failures that happen before any
    /// transport sees a request — a principal resolver that throws. The listener's own logging is
    /// switched off, so without this logger such a failure is visible only as the 500 it produces.
    /// </summary>
    public SharedHttpServerManager(HttpHostingOptions? options, ILogger? logger)
    {
        _options = options ?? new HttpHostingOptions();
        _logger = logger;
    }

    /// <summary>
    /// Returns the principal the host resolved for this request (<see cref="PrincipalItem"/>), or null.
    /// This is what a consumer copies onto the exchange it builds from the request.
    /// </summary>
    public static ClaimsPrincipal? GetResolvedPrincipal(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Items.TryGetValue(PrincipalItem, out var raw) ? raw as ClaimsPrincipal : null;
    }

    /// <summary>The process-wide host options this manager applies to every listener it opens.</summary>
    public HttpHostingOptions Options => _options;

    /// <summary>Number of active servers.</summary>
    public int ServerCount => _servers.Count;

    /// <summary>
    /// Registers a route handler on the shared server for the given host:port.
    /// Creates the server if it does not yet exist.
    /// </summary>
    /// <param name="host">Bind address (e.g. "0.0.0.0").</param>
    /// <param name="port">Bind port.</param>
    /// <param name="pathTemplate">Route path template (e.g. "/api/users/{id}").</param>
    /// <param name="methods">Allowed HTTP methods (comma-separated) or null for all.</param>
    /// <param name="handler">The async request handler delegate.</param>
    /// <param name="ssl">Whether this server uses HTTPS.</param>
    /// <param name="sslCertPath">PFX certificate path for HTTPS.</param>
    /// <param name="sslCertPassword">PFX certificate password.</param>
    /// <param name="corsOptions">Optional per-route CORS configuration. When supplied, the
    /// shared server installs a single CORS middleware that dispatches per matched route, so
    /// different routes on the same <c>(host, port)</c> can declare different CORS policies.
    /// When null, no CORS headers are emitted for this route.</param>
    /// <param name="maxRequestBodySize">Max request body size (0 = unlimited).</param>
    /// <param name="protocol">HTTP protocol set for the listener. Every route on a given
    /// <c>(host, port)</c> must agree — a mismatch throws rather than silently keeping the first value.</param>
    /// <param name="clientCertificateMode">Client-certificate policy (mTLS). Belongs to the TLS
    /// handshake, so it is per listener and must agree across routes on the same port.</param>
    /// <param name="clientCertificateValidation">Extra check on a presented client certificate, applied
    /// on top of Kestrel's chain validation (e.g. a thumbprint allow-list).</param>
    /// <param name="concurrencyLimit">Optional per-registration admission limit: caps concurrent
    /// executions of <paramref name="handler"/> and sheds the overflow with a status reply before
    /// any pipeline work. Strictly per-route — neighbors on the same listener are unaffected.</param>
    /// <returns>A registration handle that can be used to unregister the route.</returns>
    public RouteRegistration RegisterRoute(
        string host,
        int port,
        string pathTemplate,
        string? methods,
        Func<HttpContext, Task> handler,
        bool ssl = false,
        string? sslCertPath = null,
        string? sslCertPassword = null,
        RouteCorsOptions? corsOptions = null,
        long maxRequestBodySize = 0,
        HttpProtocol protocol = HttpProtocol.Http1And2,
        ClientCertificateMode? clientCertificateMode = null,
        Func<X509Certificate2, X509Chain?, SslPolicyErrors, bool>? clientCertificateValidation = null,
        ConcurrencyLimitOptions? concurrencyLimit = null)
    {
        concurrencyLimit?.Validate();
        var key = BuildKey(host, port);

        lock (_lock)
        {
            var entry = _servers.GetOrAdd(key, _ => new ServerEntry(host, port, ssl, sslCertPath, sslCertPassword,
                maxRequestBodySize, protocol, clientCertificateMode, clientCertificateValidation));

            // Validate scheme consistency
            if (entry.Ssl != ssl)
                throw new InvalidOperationException(
                    $"Server on {host}:{port} is already registered as {(entry.Ssl ? "HTTPS" : "HTTP")}. " +
                    $"Cannot register a {(ssl ? "HTTPS" : "HTTP")} route on the same port.");

            // One listener cannot speak two protocol sets. Silently keeping the first value used to send
            // a gRPC route to an HTTP/1.1 listener, where every call fails with an unreadable framing
            // error instead of a configuration one.
            if (entry.Protocol != protocol)
                throw new InvalidOperationException(
                    $"Server on {host}:{port} is already listening as {entry.Protocol}. " +
                    $"Cannot register a {protocol} route on the same port — use a separate port.");

            // Same reasoning for mTLS: the client-certificate policy belongs to the TLS handshake, so it
            // is per listener, not per route.
            if (entry.ClientCertificateMode != clientCertificateMode)
                throw new InvalidOperationException(
                    $"Server on {host}:{port} is already registered with client-certificate mode " +
                    $"'{entry.ClientCertificateMode?.ToString() ?? "none"}'. Cannot register a route with " +
                    $"'{clientCertificateMode?.ToString() ?? "none"}' on the same port.");

            // Admission limit is strictly per-registration: several routes share one listener, and a
            // saturated neighbor must not eat this route's budget (nor the other way around).
            System.Threading.RateLimiting.ConcurrencyLimiter? limiter = null;
            if (concurrencyLimit is not null)
            {
                limiter = concurrencyLimit.CreateLimiter();
                handler = WrapWithConcurrencyLimit(handler, concurrencyLimit, limiter);
            }

            var registration = new RouteRegistration(key, pathTemplate, methods, handler, corsOptions)
            {
                Limiter = limiter,
            };
            entry.Routes.Add(registration);
            entry.InvalidateRouteTable();

            // Track whether the server needs to install the CORS dispatch middleware.
            // Once any route registers with CORS, the middleware is enabled for the lifetime
            // of the server (it is a no-op for routes that do not have CORS configured).
            if (corsOptions is not null)
                entry.CorsEnabled = true;

            return registration;
        }
    }

    /// <summary>
    /// Declares that the listener on <paramref name="host"/>:<paramref name="port"/> serves WebSocket
    /// upgrades, so <c>UseWebSockets()</c> is installed when the server is built. Opt-in per listener:
    /// a listener nobody asked for WebSockets on keeps the pipeline the HTTP-only transports have
    /// always had, byte for byte.
    /// <para>
    /// Must be called BEFORE the server starts. Middleware cannot be added to a built
    /// <see cref="WebApplication"/>, and silently ignoring the call would produce a listener that
    /// rejects every upgrade with no explanation. In a route context this is satisfied naturally:
    /// endpoints start before consumers, and only a consumer calls <see cref="EnsureStarted"/>.
    /// </para>
    /// </summary>
    /// <param name="host">Listener host.</param>
    /// <param name="port">Listener port.</param>
    /// <param name="keepAliveInterval">
    /// Ping interval for idle connections. It is a property of the listener, not of a route, so the
    /// first non-null value wins and later ones are ignored.
    /// </param>
    public void EnableWebSockets(string host, int port, TimeSpan? keepAliveInterval = null,
        bool ssl = false, string? sslCertPath = null, string? sslCertPassword = null)
    {
        var key = BuildKey(host, port);

        lock (_lock)
        {
            // The TLS settings travel with the call because this may be the first touch of the
            // listener: an entry created as plain HTTP here would then collide with the wss route
            // the consumer registers a moment later.
            var entry = _servers.GetOrAdd(key, _ => new ServerEntry(host, port, ssl, sslCertPath, sslCertPassword,
                0, HttpProtocol.Http1And2, null, null));

            // Idempotent: asking again for something the listener already has is a no-op, so a
            // consumer may repeat the call its endpoint already made. Only a listener that was
            // built WITHOUT the middleware is a real problem, and that one fails loud.
            if (entry.IsStarted)
            {
                if (entry.WebSocketsEnabled) return;

                throw new InvalidOperationException(
                    $"Server on {host}:{port} is already started without WebSocket support. " +
                    "Enable it before the listener starts — from the endpoint's Start, which the route " +
                    "context runs before any consumer.");
            }

            entry.WebSocketsEnabled = true;
            entry.WebSocketKeepAlive ??= keepAliveInterval;
        }
    }

    /// <summary>
    /// Registers configuration applied when the listener's <see cref="WebApplication"/> is built:
    /// <paramref name="services"/> before <c>Build()</c>, <paramref name="endpoints"/> after it and
    /// before the catch-all route. This is what lets a transport bring its own framework pieces
    /// (SignalR hubs, authentication middleware, a backplane) without this package taking a
    /// dependency on any of them.
    /// <para>
    /// Note that the shared host builds its own service container: it does not see the services of
    /// the application that hosts the route context, so everything a configurator needs must be
    /// registered by that configurator.
    /// </para>
    /// <para>Must be called BEFORE the server starts, for the same reason as <see cref="EnableWebSockets"/>.</para>
    /// </summary>
    public ServerConfiguratorRegistration RegisterServerConfigurator(
        string host,
        int port,
        Action<IServiceCollection>? services = null,
        Action<WebApplication>? endpoints = null,
        bool ssl = false,
        string? sslCertPath = null,
        string? sslCertPassword = null)
    {
        if (services is null && endpoints is null)
            throw new ArgumentException("At least one of services/endpoints must be supplied.");

        var key = BuildKey(host, port);

        lock (_lock)
        {
            // A transport that maps its own endpoints (a SignalR hub) may never call RegisterRoute,
            // so this can be the only place its TLS settings ever reach the listener.
            var entry = _servers.GetOrAdd(key, _ => new ServerEntry(host, port, ssl, sslCertPath, sslCertPassword,
                0, HttpProtocol.Http1And2, null, null));

            if (ssl && !entry.Ssl)
                throw new InvalidOperationException(
                    $"Server on {host}:{port} is already registered as HTTP. Cannot add an HTTPS hub to the same port.");

            if (entry.IsStarted)
                throw new InvalidOperationException(
                    $"Server on {host}:{port} is already started — configurators must be registered before it starts. " +
                    "Register them from the endpoint's Start (which runs before consumers).");

            var registration = new ServerConfiguratorRegistration(host, port, services, endpoints);
            entry.Configurators.Add(registration);
            return registration;
        }
    }

    /// <summary>
    /// Removes a configurator, so a transport that maps itself onto the listener can leave it the
    /// way an HTTP consumer leaves by unregistering its route. The listener keeps running until
    /// the last route AND the last configurator are gone.
    /// </summary>
    public void UnregisterServerConfigurator(ServerConfiguratorRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);

        var key = BuildKey(registration.Host, registration.Port);

        lock (_lock)
        {
            if (_servers.TryGetValue(key, out var entry))
                entry.Configurators.Remove(registration);
        }
    }

    /// <summary>
    /// Unregisters a route. If no routes remain on the server, it will be stopped on next StopServer call.
    /// </summary>
    public void UnregisterRoute(RouteRegistration registration)
    {
        lock (_lock)
        {
            if (_servers.TryGetValue(registration.ServerKey, out var entry))
            {
                entry.Routes.Remove(registration);
                entry.InvalidateRouteTable();
            }
        }

        // Outside the lock: releases queued waiters (their leases come back non-acquired → reject).
        registration.Limiter?.Dispose();
    }

    /// <summary>
    /// Wraps a route handler with the admission limit: acquire a permit (waiting in the FIFO queue
    /// when one is configured), or shed the request with the configured status + Retry-After before
    /// any pipeline work. The wait is bound to <c>RequestAborted</c>, so a client that gives up
    /// leaves the queue instead of holding a slot.
    /// </summary>
    private static Func<HttpContext, Task> WrapWithConcurrencyLimit(
        Func<HttpContext, Task> inner,
        ConcurrencyLimitOptions limit,
        System.Threading.RateLimiting.ConcurrencyLimiter limiter)
    {
        return async ctx =>
        {
            using var lease = await limiter.AcquireAsync(1, ctx.RequestAborted).ConfigureAwait(false);
            if (!lease.IsAcquired)
            {
                limit.OnRejected?.Invoke();
                ctx.Response.StatusCode = limit.RejectStatusCode;
                if (limit.RetryAfterSeconds > 0)
                    ctx.Response.Headers.RetryAfter = limit.RetryAfterSeconds.ToString();
                return;
            }

            await inner(ctx).ConfigureAwait(false);
        };
    }

    /// <summary>
    /// Ensures the server for the given host:port is started.
    /// If already running, this is a no-op.
    /// </summary>
    public async Task EnsureStarted(string host, int port, CancellationToken ct = default)
    {
        var key = BuildKey(host, port);

        ServerEntry entry;
        lock (_lock)
        {
            if (!_servers.TryGetValue(key, out entry!))
                throw new InvalidOperationException($"No routes registered for {host}:{port}.");
        }

        if (entry.IsStarted)
            return;

        await StartServer(entry, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Stops the server for the given host:port if it has no remaining routes.
    /// </summary>
    public async Task StopIfEmpty(string host, int port, CancellationToken ct = default)
    {
        var key = BuildKey(host, port);

        ServerEntry? entry;
        lock (_lock)
        {
            if (!_servers.TryGetValue(key, out entry))
                return;

            // A hub occupies the listener without registering a route, so both have to be empty
            // before the socket can go away under a transport that is still serving.
            if (entry.Routes.Count > 0 || entry.Configurators.Count > 0)
                return;

            _servers.TryRemove(key, out _);
        }

        await StopServer(entry, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Gets the base URL for a server. Available after EnsureStarted.
    /// </summary>
    public string? GetBaseUrl(string host, int port)
    {
        var key = BuildKey(host, port);
        if (_servers.TryGetValue(key, out var entry) && entry.IsStarted)
        {
            var scheme = entry.Ssl ? "https" : "http";
            var displayHost = host == "0.0.0.0" ? "localhost" : host;
            return $"{scheme}://{displayHost}:{port}";
        }
        return null;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        foreach (var entry in _servers.Values)
        {
            await StopServer(entry, CancellationToken.None).ConfigureAwait(false);
        }
        _servers.Clear();
    }

    // ── Private ──

    /// <summary>
    /// Turns the host of an endpoint URI into an address Kestrel can bind. Plain
    /// <c>IPAddress.Parse</c> threw a bare <c>FormatException</c> on any name; a DNS name is
    /// resolved here, and a name that resolves to nothing fails with a message that names the
    /// host. <c>localhost</c> never reaches this method — it has its own Kestrel helper.
    /// </summary>
    private static IPAddress ResolveBindAddress(string host)
    {
        if (IPAddress.TryParse(host, out var parsed))
            return parsed;

        if (string.IsNullOrWhiteSpace(host) || host == "*" || host == "+")
            return IPAddress.Any;

        try
        {
            var resolved = Dns.GetHostAddresses(host);
            if (resolved.Length > 0)
                return resolved[0];
        }
        catch (Exception ex) when (ex is SocketException or ArgumentException)
        {
            throw new ArgumentException(
                $"Cannot bind a listener to host '{host}': it is neither an IP address nor a resolvable name. " +
                "Use an address the machine owns, 0.0.0.0 for every interface, or localhost.", ex);
        }

        throw new ArgumentException(
            $"Cannot bind a listener to host '{host}': the name resolved to no addresses.");
    }

    /// <summary>
    /// Where a TLS listener's certificate comes from: the endpoint, then the host-wide default.
    /// Resolved before the host is built, so a misconfiguration fails at the bind with a message
    /// about the certificate rather than somewhere inside Kestrel's startup.
    /// </summary>
    private (System.Security.Cryptography.X509Certificates.X509Certificate2? Certificate, string? Path, string? Password)
        ResolveServerCertificate(ServerEntry entry)
    {
        if (!entry.Ssl)
            return (null, null, null);

        if (!string.IsNullOrEmpty(entry.SslCertPath))
            return (null, entry.SslCertPath, entry.SslCertPassword);

        if (_options.Tls.DefaultCertificate is { } shared)
            return (shared, null, null);

        if (!string.IsNullOrEmpty(_options.Tls.DefaultCertificatePath))
            return (null, _options.Tls.DefaultCertificatePath, _options.Tls.DefaultCertificatePassword);

        // Historically this fell through to a PLAINTEXT listener while GetBaseUrl and every log
        // line reported https://. Refusing to bind is what nginx, httpd, Jetty, Spring Boot and
        // Kestrel's own UseHttps() all do: TLS asked for and no certificate is a configuration
        // error, never a downgrade.
        throw new InvalidOperationException(
            $"Listener on {entry.Host}:{entry.Port} is configured for TLS but no server certificate could be " +
            "resolved. Set it on the endpoint (sslCertPath/sslCertPassword, or a named connectionFactory " +
            "carrying them), or give the host a default: " +
            "AddRedbRouteHttpHosting(o => o.Tls.DefaultCertificatePath = ...). Refusing to bind, because " +
            "without a certificate the listener would serve plaintext while reporting https://.");
    }

    private async Task StartServer(ServerEntry entry, CancellationToken ct)
    {
        if (entry.IsStarted) return;

        var certificate = ResolveServerCertificate(entry);

        var builder = WebApplication.CreateSlimBuilder();

        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            var protocols = entry.Protocol switch
            {
                HttpProtocol.Http1 => Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http1,
                HttpProtocol.Http2 => Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http2,
                HttpProtocol.Http1And2 => Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http1AndHttp2,
                HttpProtocol.Http3 => Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http3,
                HttpProtocol.Http1And2And3 => Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http1AndHttp2AndHttp3,
                _ => Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http1AndHttp2
            };

            void Configure(ListenOptions listenOptions)
            {
                listenOptions.Protocols = protocols;

                if (!entry.Ssl) return;

                void ConfigureHttps(HttpsConnectionAdapterOptions httpsOptions)
                {
                    if (entry.ClientCertificateMode is { } mode)
                        httpsOptions.ClientCertificateMode = mode;

                    if (entry.ClientCertificateValidation is { } validate)
                        httpsOptions.ClientCertificateValidation = (cert, chain, errors) => validate(cert, chain, errors);
                }

                if (certificate.Certificate is { } instance)
                    listenOptions.UseHttps(instance, ConfigureHttps);
                else
                    listenOptions.UseHttps(certificate.Path!, certificate.Password, ConfigureHttps);
            }

            // "localhost" goes through Kestrel's own helper, which binds BOTH loopbacks: picking one
            // IP for it would leave a client that resolved localhost to the other one unable to
            // connect. Everything else is an address, or a name resolved to one.
            if (string.Equals(entry.Host, "localhost", StringComparison.OrdinalIgnoreCase))
                kestrel.ListenLocalhost(entry.Port, Configure);
            else
                kestrel.Listen(ResolveBindAddress(entry.Host), entry.Port, Configure);

            if (entry.MaxRequestBodySize > 0)
                kestrel.Limits.MaxRequestBodySize = entry.MaxRequestBodySize;
            else
                kestrel.Limits.MaxRequestBodySize = null;

            // Host-wide connection backstop (HostConnectionLimits): Kestrel's own defaults are
            // unlimited; leave them untouched unless the deployment set a ceiling.
            if (_options.Limits.MaxConcurrentConnections is { } maxConn)
                kestrel.Limits.MaxConcurrentConnections = maxConn;
            if (_options.Limits.MaxConcurrentUpgradedConnections is { } maxUpgraded)
                kestrel.Limits.MaxConcurrentUpgradedConnections = maxUpgraded;
        });

        builder.Logging.ClearProviders();

        // Transport-supplied service registrations (SignalR hubs, authentication, a backplane).
        // Applied before Build() because that is the only moment a container can still be changed.
        foreach (var registration in entry.Configurators.ToArray())
            registration.Services?.Invoke(builder.Services);

        var app = builder.Build();

        // Trusted-proxy resolution. Installed first, before anything reads the connection: every
        // consumer on this listener (Http, Soap, As2, Grpc, the Tsak management API) takes the client
        // address from Connection.RemoteIpAddress and the URL scheme from Request.Scheme, so rewriting
        // them here is the one place that covers all of them. Not installed at all when no proxy is
        // trusted, which keeps the default host byte-for-byte what it was.
        if (_options.TrustedProxies.IsEnabled)
        {
            app.Use(TrustedProxyMiddleware(_options.TrustedProxies));
        }

        // Per-route CORS dispatch middleware.
        // Installed once per server. For each request it locates the matching route (path-only,
        // method-agnostic so that OPTIONS preflight finds the underlying route) and applies
        // that route's CORS policy. Routes registered without CORS get no headers.
        if (entry.CorsEnabled)
        {
            app.Use(CorsDispatchMiddleware(entry));
        }

        // Caller identity (HttpHostingOptions.ResolvePrincipal), opt-in per process. After the proxy
        // resolution so the resolver sees the real client, after CORS so a preflight never reaches
        // it, and before the transport configurators so a hub's own gate sees the host's principal.
        if (_options.ResolvePrincipal is { } resolvePrincipal)
        {
            app.Use(PrincipalMiddleware(resolvePrincipal, _logger));
        }

        // WebSocket upgrades, opt-in per listener (see EnableWebSockets). Installed after the
        // proxy/CORS middleware so an upgrade request is seen with its real client address, and
        // before the catch-all so a handler can accept the socket.
        if (entry.WebSocketsEnabled)
        {
            var wsOptions = new WebSocketOptions();
            if (entry.WebSocketKeepAlive is { } keepAlive)
                wsOptions.KeepAliveInterval = keepAlive;
            app.UseWebSockets(wsOptions);
        }

        // Transport-supplied endpoints (SignalR MapHub and friends). Registered before the
        // catch-all: the catch-all matches every path, and relying on route precedence to sort
        // that out is not something to leave implicit.
        foreach (var registration in entry.Configurators.ToArray())
            registration.Endpoints?.Invoke(app);

        // Single catch-all handler — our RouteTable dispatches dynamically
        app.Map("/{**path}", (HttpContext ctx) => HandleCatchAll(entry, ctx));
        // Also handle root path
        app.MapGet("/", (HttpContext ctx) => HandleCatchAll(entry, ctx));
        app.MapPost("/", (HttpContext ctx) => HandleCatchAll(entry, ctx));
        app.MapPut("/", (HttpContext ctx) => HandleCatchAll(entry, ctx));
        app.MapDelete("/", (HttpContext ctx) => HandleCatchAll(entry, ctx));
        app.MapMethods("/", ["PATCH", "HEAD", "OPTIONS"], (HttpContext ctx) => HandleCatchAll(entry, ctx));

        entry.App = app;
        await app.StartAsync(ct).ConfigureAwait(false);
        entry.IsStarted = true;
    }

    private static async Task HandleCatchAll(ServerEntry entry, HttpContext ctx)
    {
        var requestPath = ctx.Request.Path.Value ?? "/";
        var requestMethod = ctx.Request.Method;

        // Find matching route
        var match = entry.MatchRoute(requestPath, requestMethod);

        if (match.Registration is null)
        {
            // If path matched but method didn't, return 405
            ctx.Response.StatusCode = match.PathMatched
                ? StatusCodes.Status405MethodNotAllowed
                : StatusCodes.Status404NotFound;
            return;
        }

        // Store route values for handlers to extract
        if (match.RouteValues is not null)
        {
            ctx.Items["__redbRouteValues"] = match.RouteValues;
        }

        await match.Registration.Handler(ctx).ConfigureAwait(false);
    }

    /// <summary>
    /// Builds the trusted-proxy delegate for one listener. It hands the socket peer and the two
    /// forwarded headers to <see cref="ForwardedHeaderResolver"/> and writes the outcome back into
    /// the connection and the request, keeping the originals in <c>HttpContext.Items</c> under
    /// <see cref="OriginalRemoteAddressItem"/> / <see cref="OriginalSchemeItem"/> for diagnostics.
    /// All the policy lives in the resolver; this is the ten lines that know about Kestrel.
    /// </summary>
    private static Func<HttpContext, Func<Task>, Task> TrustedProxyMiddleware(TrustedProxyOptions trust)
    {
        return (ctx, next) =>
        {
            var peer = ctx.Connection.RemoteIpAddress;
            var headers = ctx.Request.Headers;

            var resolved = ForwardedHeaderResolver.Resolve(
                peer,
                headers[ForwardedHeaderResolver.ForwardedFor].ToString(),
                headers[ForwardedHeaderResolver.ForwardedProto].ToString(),
                trust);

            if (resolved.AddressApplied && resolved.ClientAddress is not null)
            {
                ctx.Items[OriginalRemoteAddressItem] = peer;
                ctx.Connection.RemoteIpAddress = resolved.ClientAddress;
            }

            if (trust.ForwardScheme && resolved.Scheme is not null &&
                !string.Equals(resolved.Scheme, ctx.Request.Scheme, StringComparison.OrdinalIgnoreCase))
            {
                ctx.Items[OriginalSchemeItem] = ctx.Request.Scheme;
                ctx.Request.Scheme = resolved.Scheme;
            }

            return next();
        };
    }

    /// <summary>
    /// Builds the caller-identity delegate: runs the host's resolver once per request and keeps a
    /// non-null result in <c>HttpContext.Items</c> under <see cref="PrincipalItem"/>. An anonymous
    /// request passes through untouched; a request whose resolver failed does not, because letting it
    /// through would hand the route an anonymous caller that may not be one.
    /// </summary>
    private static Func<HttpContext, Func<Task>, Task> PrincipalMiddleware(
        Func<HttpContext, Task<ClaimsPrincipal?>> resolve, ILogger? logger)
    {
        return async (ctx, next) =>
        {
            ClaimsPrincipal? principal;
            try
            {
                principal = await resolve(ctx).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The text stays in the log: it describes the resolver (a key endpoint, a token store),
                // nothing the caller should read.
                logger?.LogError(ex, "Principal resolver failed for {Method} {Path}",
                    ctx.Request.Method, ctx.Request.Path);
                ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
                return;
            }

            if (principal is not null)
                ctx.Items[PrincipalItem] = principal;

            await next().ConfigureAwait(false);
        };
    }

    /// <summary>
    /// Builds the per-route CORS dispatch middleware. The middleware locates the route by
    /// path (method-agnostic so OPTIONS preflight resolves the underlying route's policy),
    /// then applies that route's <see cref="RouteCorsOptions"/>:
    /// <list type="number">
    /// <item>resolves the allowed origin via the resolver delegate or the static whitelist;</item>
    /// <item>echoes the resolved origin in <c>Access-Control-Allow-Origin</c> with <c>Vary: Origin</c>;</item>
    /// <item>reflects requested headers and methods on preflight (RFC 6454 §7.3 friendly);</item>
    /// <item>guards the wildcard-with-credentials footgun at request time.</item>
    /// </list>
    /// Routes without CORS options receive no headers \u2014 the middleware is transparent for them.
    /// </summary>
    private static Func<HttpContext, Func<Task>, Task> CorsDispatchMiddleware(ServerEntry entry)
    {
        return async (ctx, next) =>
        {
            var requestPath = ctx.Request.Path.Value ?? "/";
            var route = entry.MatchByPath(requestPath);
            var cors = route?.Cors;

            if (cors is null)
            {
                await next().ConfigureAwait(false);
                return;
            }

            var requestOrigin = ctx.Request.Headers["Origin"].ToString();
            var resolved = ResolveOrigin(cors, ctx.Request, requestOrigin);

            // C15 / browser footgun: wildcard with credentials is rejected by browsers.
            // Demote to "no CORS headers" so the request fails closed instead of emitting
            // headers the browser will refuse and confusing the developer.
            if (resolved == "*" && cors.AllowCredentials)
                resolved = null;

            if (resolved is not null)
            {
                ctx.Response.Headers["Access-Control-Allow-Origin"] = resolved;

                // Vary: Origin is mandatory whenever the response varies by request Origin
                // (i.e. always when a resolver/whitelist is in play). Without it, intermediate
                // caches will serve the wrong CORS headers to the wrong origin.
                AppendVary(ctx.Response.Headers, "Origin");

                if (cors.AllowCredentials)
                    ctx.Response.Headers["Access-Control-Allow-Credentials"] = "true";

                // Preflight reflection: echo back exactly what the browser asked for, falling
                // back to the route's static method list or "*" only when the request did not
                // advertise a specific method/headers set.
                if (HttpMethods.IsOptions(ctx.Request.Method))
                {
                    var requestedMethod = ctx.Request.Headers["Access-Control-Request-Method"].ToString();
                    ctx.Response.Headers["Access-Control-Allow-Methods"] = !string.IsNullOrEmpty(requestedMethod)
                        ? requestedMethod
                        : (cors.AllowedMethods ?? route!.Methods ?? "*");

                    var requestedHeaders = ctx.Request.Headers["Access-Control-Request-Headers"].ToString();
                    ctx.Response.Headers["Access-Control-Allow-Headers"] = !string.IsNullOrEmpty(requestedHeaders)
                        ? requestedHeaders
                        : (cors.AllowCredentials ? "Content-Type, Authorization" : "*");

                    ctx.Response.Headers["Access-Control-Max-Age"] = cors.MaxAgeSeconds.ToString();
                }
            }

            // Always short-circuit OPTIONS preflight \u2014 even when the origin was rejected,
            // returning 204 with no CORS headers is the correct browser-friendly behaviour
            // (browser will then reject the request without escalating to the actual call).
            if (HttpMethods.IsOptions(ctx.Request.Method))
            {
                ctx.Response.StatusCode = StatusCodes.Status204NoContent;
                return;
            }

            await next().ConfigureAwait(false);
        };
    }

    private static string? ResolveOrigin(RouteCorsOptions cors, HttpRequest request, string requestOrigin)
    {
        // Resolver delegate wins; allowed to return "*", a specific origin, or null.
        if (cors.OriginsResolver is { } resolver)
            return resolver(request);

        var allowed = cors.AllowedOrigins;
        if (string.IsNullOrEmpty(allowed))
            return null;

        // Wildcard whitelist: emit "*" verbatim. Browsers ignore "*" with credentials \u2014
        // the caller-side guard above demotes that combination to null.
        if (allowed == "*")
            return "*";

        // Explicit whitelist: only echo back the request's Origin if it appears in the
        // comma-separated list. Browsers cannot consume a CSV in Access-Control-Allow-Origin,
        // so we must single-select.
        if (string.IsNullOrEmpty(requestOrigin))
            return null;

        foreach (var entry in allowed.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (entry.Equals(requestOrigin, StringComparison.OrdinalIgnoreCase))
                return entry;
        }
        return null;
    }

    private static void AppendVary(IHeaderDictionary headers, string value)
    {
        var current = headers["Vary"].ToString();
        if (string.IsNullOrEmpty(current))
        {
            headers["Vary"] = value;
            return;
        }
        // Avoid duplicating "Origin" if some other middleware already added it.
        foreach (var part in current.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.Equals(value, StringComparison.OrdinalIgnoreCase))
                return;
        }
        headers["Vary"] = current + ", " + value;
    }

    private static async Task StopServer(ServerEntry entry, CancellationToken ct)
    {
        if (entry.App is not null && entry.IsStarted)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                await entry.App.StopAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }

            await entry.App.DisposeAsync().ConfigureAwait(false);
            entry.App = null;
            entry.IsStarted = false;
        }
    }

    private static string BuildKey(string host, int port) => $"{host}:{port}";

    // ── Inner types ──

    internal sealed class ServerEntry
    {
        public string Host { get; }
        public int Port { get; }
        public bool Ssl { get; }
        public string? SslCertPath { get; }
        public string? SslCertPassword { get; }
        public long MaxRequestBodySize { get; }
        public HttpProtocol Protocol { get; }
        public List<RouteRegistration> Routes { get; } = [];
        public WebApplication? App { get; set; }
        public bool IsStarted { get; set; }

        /// <summary>
        /// True when at least one registered route declared CORS options. The shared CORS
        /// dispatch middleware is installed iff this is true at server-build time.
        /// </summary>
        public bool CorsEnabled { get; set; }

        /// <summary>
        /// True when a transport on this listener needs WebSocket upgrades (ws, wss, SignalR).
        /// Opt-in per listener: without it the pipeline stays exactly what the HTTP-only
        /// transports have always had.
        /// </summary>
        public bool WebSocketsEnabled { get; set; }

        /// <summary>Keep-alive interval for the WebSocket middleware; null leaves the framework default.</summary>
        public TimeSpan? WebSocketKeepAlive { get; set; }

        /// <summary>
        /// Transports that configure the listener itself instead of registering a route (a SignalR
        /// hub maps itself). They are occupants of the listener just like routes: the server stays
        /// up while any of them is present, and stops when the last one leaves.
        /// </summary>
        public List<ServerConfiguratorRegistration> Configurators { get; } = [];

        private readonly TemplateMatcher[] _matchers = [];
        private volatile (TemplateMatcher matcher, RouteRegistration reg, string[]? methods)[]? _compiled;

        /// <summary>
        /// Drops the compiled route table so the next request rebuilds it. Called on every register and
        /// unregister: a count comparison missed an unregister + register pair (a route restart, a REST
        /// redeploy) and kept dispatching to a removed handler.
        /// </summary>
        public void InvalidateRouteTable() => _compiled = null;

        /// <summary>Client-certificate policy for the listener (mTLS). Null = do not request one.</summary>
        public ClientCertificateMode? ClientCertificateMode { get; }

        /// <summary>
        /// Extra check applied to a presented client certificate on top of Kestrel's chain validation
        /// (for example an allow-list of thumbprints).
        /// </summary>
        public Func<X509Certificate2, X509Chain?, SslPolicyErrors, bool>? ClientCertificateValidation { get; }

        public ServerEntry(string host, int port, bool ssl, string? sslCertPath, string? sslCertPassword,
            long maxRequestBodySize, HttpProtocol protocol = HttpProtocol.Http1And2,
            ClientCertificateMode? clientCertificateMode = null,
            Func<X509Certificate2, X509Chain?, SslPolicyErrors, bool>? clientCertificateValidation = null)
        {
            Host = host;
            Port = port;
            Ssl = ssl;
            SslCertPath = sslCertPath;
            SslCertPassword = sslCertPassword;
            MaxRequestBodySize = maxRequestBodySize;
            Protocol = protocol;
            ClientCertificateMode = clientCertificateMode;
            ClientCertificateValidation = clientCertificateValidation;
        }

        public RouteMatch MatchRoute(string requestPath, string requestMethod)
        {
            // Compile route table lazily / on change
            var compiled = GetCompiled();
            var pathMatched = false;

            foreach (var (matcher, reg, methods) in compiled)
            {
                var values = new RouteValueDictionary();
                if (!matcher.TryMatch(requestPath, values))
                    continue;

                pathMatched = true;

                // Check method filter
                if (methods is not null && !methods.Any(m => m.Equals(requestMethod, StringComparison.OrdinalIgnoreCase)))
                    continue;

                return new RouteMatch(reg, values, true);
            }

            return new RouteMatch(null, null, pathMatched);
        }

        /// <summary>
        /// Returns the first route whose path template matches <paramref name="requestPath"/>,
        /// regardless of HTTP method. Used by the CORS dispatch middleware so that an OPTIONS
        /// preflight can locate the underlying route's CORS policy even when OPTIONS is not
        /// in the route's allowed-methods list.
        /// </summary>
        public RouteRegistration? MatchByPath(string requestPath)
        {
            var compiled = GetCompiled();
            foreach (var (matcher, reg, _) in compiled)
            {
                var values = new RouteValueDictionary();
                if (matcher.TryMatch(requestPath, values))
                    return reg;
            }
            return null;
        }

        private (TemplateMatcher matcher, RouteRegistration reg, string[]? methods)[] GetCompiled()
        {
            var compiled = _compiled;
            if (compiled is not null)
                return compiled;

            // Build, then order by specificity so a concrete path (e.g. "/api/echo") is matched
            // BEFORE a catch-all ("/{**path}") registered on the same (host, port). Without this
            // the table was pure registration order and a catch-all would swallow every later
            // route. Specificity ranking (literal-heavy first, catch-all last) matches what
            // callers intuitively expect from ASP.NET-style routing. Registration order is the
            // stable tie-breaker, so equal-specificity routes keep their old first-wins behaviour.
            var built = new (TemplateMatcher matcher, RouteRegistration reg, string[]? methods, int order, RouteSpecificity spec)[Routes.Count];
            for (var i = 0; i < Routes.Count; i++)
            {
                var reg = Routes[i];
                var template = TemplateParser.Parse(reg.PathTemplate.TrimStart('/'));
                var matcher = new TemplateMatcher(template, new RouteValueDictionary());
                var methods = ParseMethods(reg.Methods);
                built[i] = (matcher, reg, methods, i, Analyze(template));
            }

            _compiled = built
                .OrderBy(x => x.spec.HasCatchAll ? 1 : 0)      // concrete paths first, catch-all last
                .ThenByDescending(x => x.spec.Literals)        // more literal segments = more specific
                .ThenBy(x => x.spec.Parameters)                // fewer parameters = more specific
                .ThenBy(x => x.order)                          // stable: preserve registration order on ties
                .Select(x => (x.matcher, x.reg, x.methods))
                .ToArray();
            return _compiled;
        }

        /// <summary>Specificity facts about a parsed route template, used to rank match order.</summary>
        private readonly record struct RouteSpecificity(bool HasCatchAll, int Literals, int Parameters);

        private static RouteSpecificity Analyze(RouteTemplate template)
        {
            var hasCatchAll = false;
            int literals = 0, parameters = 0;
            foreach (var segment in template.Segments)
            {
                foreach (var part in segment.Parts)
                {
                    if (part.IsCatchAll) hasCatchAll = true;
                    else if (part.IsParameter) parameters++;
                    else if (part.IsLiteral) literals++;
                }
            }
            return new RouteSpecificity(hasCatchAll, literals, parameters);
        }

        private static string[]? ParseMethods(string? methods)
        {
            if (string.IsNullOrEmpty(methods)) return null;
            return methods.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        }
    }
}

/// <summary>
/// Handle for a registered route on a shared server.
/// </summary>
public sealed class RouteRegistration
{
    internal string ServerKey { get; }

    /// <summary>The route path template.</summary>
    public string PathTemplate { get; }

    /// <summary>Allowed methods (comma-separated) or null for all methods.</summary>
    public string? Methods { get; }

    /// <summary>The request handler.</summary>
    public Func<HttpContext, Task> Handler { get; }

    /// <summary>
    /// Optional per-route CORS configuration. When set, the shared server applies these
    /// CORS headers for requests matching <see cref="PathTemplate"/>.
    /// </summary>
    public RouteCorsOptions? Cors { get; }

    /// <summary>
    /// Admission limiter backing this registration's <c>maxConcurrentRequests</c>, when one is
    /// configured. Owned by the registration; disposed on unregister.
    /// </summary>
    internal System.Threading.RateLimiting.ConcurrencyLimiter? Limiter { get; init; }

    internal RouteRegistration(string serverKey, string pathTemplate, string? methods, Func<HttpContext, Task> handler, RouteCorsOptions? cors = null)
    {
        ServerKey = serverKey;
        PathTemplate = pathTemplate;
        Methods = methods;
        Handler = handler;
        Cors = cors;
    }
}

/// <summary>
/// Handle for a listener configurator: what a transport that maps itself onto the shared host
/// (a SignalR hub) holds instead of a <see cref="RouteRegistration"/>. Holding one keeps the
/// listener alive, and giving it back is how the transport leaves.
/// </summary>
public sealed class ServerConfiguratorRegistration
{
    /// <summary>Bind host of the listener this configurator belongs to.</summary>
    public string Host { get; }

    /// <summary>Bind port of the listener this configurator belongs to.</summary>
    public int Port { get; }

    internal Action<IServiceCollection>? Services { get; }

    internal Action<WebApplication>? Endpoints { get; }

    internal ServerConfiguratorRegistration(string host, int port,
        Action<IServiceCollection>? services, Action<WebApplication>? endpoints)
    {
        Host = host;
        Port = port;
        Services = services;
        Endpoints = endpoints;
    }
}

/// <summary>
/// Per-route CORS configuration applied by the shared HTTP server's dispatch middleware.
/// All fields are immutable. The middleware enforces the wildcard-with-credentials rule
/// at request time regardless of which field the origin came from.
/// </summary>
/// <param name="AllowedOrigins">Comma-separated whitelist of allowed origins. May contain a single
/// <c>"*"</c> for public endpoints (only when <paramref name="AllowCredentials"/> is false).</param>
/// <param name="AllowedMethods">Comma-separated list emitted in <c>Access-Control-Allow-Methods</c>
/// when the request is a preflight; if null, the route's method filter is used; if also null, <c>*</c>.</param>
/// <param name="AllowCredentials">Sets <c>Access-Control-Allow-Credentials: true</c> when true.</param>
/// <param name="OriginsResolver">Optional per-request resolver. Receives the request and returns
/// the origin to echo back, <c>"*"</c>, or null (origin not allowed). Takes precedence over
/// <paramref name="AllowedOrigins"/>.</param>
/// <param name="MaxAgeSeconds">Value emitted in <c>Access-Control-Max-Age</c>. Default 86400 (24h).</param>
public sealed record RouteCorsOptions(
    string? AllowedOrigins,
    string? AllowedMethods,
    bool AllowCredentials,
    Func<Microsoft.AspNetCore.Http.HttpRequest, string?>? OriginsResolver = null,
    int MaxAgeSeconds = 86_400);

/// <summary>
/// Result of route matching with path-match and method-match distinction.
/// </summary>
internal readonly record struct RouteMatch(
    RouteRegistration? Registration,
    RouteValueDictionary? RouteValues,
    bool PathMatched);

using System.Collections.Concurrent;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Template;
using Microsoft.AspNetCore.Server.Kestrel.Core;
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
        HttpProtocol protocol = HttpProtocol.Http1And2)
    {
        var key = BuildKey(host, port);

        lock (_lock)
        {
            var entry = _servers.GetOrAdd(key, _ => new ServerEntry(host, port, ssl, sslCertPath, sslCertPassword, maxRequestBodySize, protocol));

            // Validate scheme consistency
            if (entry.Ssl != ssl)
                throw new InvalidOperationException(
                    $"Server on {host}:{port} is already registered as {(entry.Ssl ? "HTTPS" : "HTTP")}. " +
                    $"Cannot register a {(ssl ? "HTTPS" : "HTTP")} route on the same port.");

            var registration = new RouteRegistration(key, pathTemplate, methods, handler, corsOptions);
            entry.Routes.Add(registration);

            // Track whether the server needs to install the CORS dispatch middleware.
            // Once any route registers with CORS, the middleware is enabled for the lifetime
            // of the server (it is a no-op for routes that do not have CORS configured).
            if (corsOptions is not null)
                entry.CorsEnabled = true;

            return registration;
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
            }
        }
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

            if (entry.Routes.Count > 0)
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

    private async Task StartServer(ServerEntry entry, CancellationToken ct)
    {
        if (entry.IsStarted) return;

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

            if (entry.Ssl && !string.IsNullOrEmpty(entry.SslCertPath))
            {
                kestrel.Listen(IPAddress.Parse(entry.Host), entry.Port, listenOptions =>
                {
                    listenOptions.Protocols = protocols;
                    listenOptions.UseHttps(entry.SslCertPath, entry.SslCertPassword);
                });
            }
            else
            {
                kestrel.Listen(IPAddress.Parse(entry.Host), entry.Port, listenOptions =>
                {
                    listenOptions.Protocols = protocols;
                });
            }

            if (entry.MaxRequestBodySize > 0)
                kestrel.Limits.MaxRequestBodySize = entry.MaxRequestBodySize;
            else
                kestrel.Limits.MaxRequestBodySize = null;
        });

        builder.Logging.ClearProviders();

        var app = builder.Build();

        // Per-route CORS dispatch middleware.
        // Installed once per server. For each request it locates the matching route (path-only,
        // method-agnostic so that OPTIONS preflight finds the underlying route) and applies
        // that route's CORS policy. Routes registered without CORS get no headers.
        if (entry.CorsEnabled)
        {
            app.Use(CorsDispatchMiddleware(entry));
        }

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

        private readonly TemplateMatcher[] _matchers = [];
        private volatile (TemplateMatcher matcher, RouteRegistration reg, string[]? methods)[]? _compiled;

        public ServerEntry(string host, int port, bool ssl, string? sslCertPath, string? sslCertPassword, long maxRequestBodySize, HttpProtocol protocol = HttpProtocol.Http1And2)
        {
            Host = host;
            Port = port;
            Ssl = ssl;
            SslCertPath = sslCertPath;
            SslCertPassword = sslCertPassword;
            MaxRequestBodySize = maxRequestBodySize;
            Protocol = protocol;
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
            if (_compiled is not null && _compiled.Length == Routes.Count)
                return _compiled;

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

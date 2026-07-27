using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.Core;

namespace redb.Route.Http;

/// <summary>
/// HTTP/HTTPS component. Schemes: "http", "https".
/// <para>Producer: HttpClient-based HTTP caller (GET/POST/PUT/DELETE/PATCH).</para>
/// <para>Consumer: Kestrel-based embedded HTTP server (webhook receiver).</para>
/// <para>URI format: http:host:port/path?method=POST&amp;timeout=30000</para>
/// <para>URI format: https:api.example.com/orders?method=POST&amp;throwOnError=true</para>
/// <para>URI shorthand: http:GET:/path (method prefix before the path, consumer only)</para>
/// </summary>
public class HttpComponent : ComponentBase
{
    private readonly string _scheme;

    /// <summary>Creates an HTTP component with the specified scheme.</summary>
    public HttpComponent(string scheme = "http")
    {
        _scheme = scheme;
    }

    /// <inheritdoc />
    public override string Scheme => _scheme;

    /// <summary>
    /// Global CORS defaults applied to all HTTP consumers.
    /// Endpoint-level URI parameters (cors, corsOrigins, corsCredentials) override these.
    /// </summary>
    public HttpCorsOptions DefaultCors { get; } = new();

    /// <summary>
    /// Shared HTTP server manager for pooling Kestrel instances by (host, port).
    /// Must be set before creating consumers.
    /// </summary>
    public SharedHttpServerManager? ServerManager { get; set; }

    /// <inheritdoc />
    public override IEndpoint CreateEndpoint(EndpointUri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);

        var options = new HttpEndpointOptions();
        options.BindFromUri(uri.RawParameters);

        // Resolve the named ConnectionFactory right after binding — before the path is parsed, so
        // host/port taken from the URI path always win — and before Validate(), which requires
        // Username/Password when AuthScheme=Basic.
        if (!string.IsNullOrEmpty(options.ConnectionFactory) && Context is not null)
        {
            var factory = Context.GetFromRegistry<HttpConnectionFactory>(options.ConnectionFactory);
            if (factory is not null)
                factory.ApplyTo(options, uri);
            else
                Logger?.LogWarning(
                    "HTTP: ConnectionFactory '{Name}' not found in registry, falling back to URI parameters",
                    options.ConnectionFactory);
        }

        // Support shorthand: http:GET:/path or http:POST:host:port/path
        var path = ExtractMethodPrefix(uri.Path, options, uri.RawParameters, out var hasMethodPrefix);

        // Extract host[:port] from URI path (e.g. "0.0.0.0:5088/api/demo")
        // Only override if not explicitly set via query parameters.
        ParseHostPort(path, options, uri.RawParameters);

        // Merge global CORS defaults for fields not explicitly set in URI
        ApplyCorsDefaults(options, uri.RawParameters);

        options.Validate();

        return new HttpEndpoint(uri, this, options, path, hasMethodPrefix);
    }

    /// <summary>
    /// Detects an HTTP method prefix in the URI path (e.g. "GET:/connect/token")
    /// and strips it, applying the method to options. Query params always take priority.
    /// Returns the cleaned path (without the method prefix).
    /// </summary>
    internal static string ExtractMethodPrefix(
        string path, HttpEndpointOptions options,
        IReadOnlyDictionary<string, string> rawParams,
        out bool hasMethodPrefix)
    {
        hasMethodPrefix = false;
        var colonIdx = path.IndexOf(':');
        if (colonIdx <= 0)
            return path;

        var candidate = path[..colonIdx];
        if (!Enum.TryParse<HttpMethod>(candidate, ignoreCase: true, out var method))
            return path;

        // Method prefix found — strip it from path
        hasMethodPrefix = true;
        var cleanPath = path[(colonIdx + 1)..];

        // URI query params always take priority
        if (!rawParams.ContainsKey("method"))
            options.Method = method;
        if (!rawParams.ContainsKey("methods"))
            options.Methods = candidate.ToUpperInvariant();

        return cleanPath;
    }

    /// <summary>
    /// Extracts host and port from the URI path segment (host[:port][/path]).
    /// Only overrides options if not already set via query parameters.
    /// </summary>
    private static void ParseHostPort(string path, HttpEndpointOptions options, IReadOnlyDictionary<string, string> rawParams)
    {
        // Path format: "host:port/path" or "host/path" or "host:port" or "host"
        var segment = path.TrimStart('/');
        var slashIdx = segment.IndexOf('/');
        var hostPort = slashIdx >= 0 ? segment[..slashIdx] : segment;

        var colonIdx = hostPort.LastIndexOf(':');
        if (colonIdx > 0)
        {
            var hostPart = hostPort[..colonIdx];
            var portPart = hostPort[(colonIdx + 1)..];

            if (!rawParams.ContainsKey("host") && !string.IsNullOrEmpty(hostPart))
                options.Host = hostPart;

            if (!rawParams.ContainsKey("port") && int.TryParse(portPart, out var port))
                options.Port = port;
        }
        else if (!rawParams.ContainsKey("host") && !string.IsNullOrEmpty(hostPort))
        {
            options.Host = hostPort;
        }
    }

    /// <summary>
    /// Applies global CORS defaults to options where the URI did not explicitly set them.
    /// Endpoint-level parameters always win over component-level defaults.
    /// </summary>
    private void ApplyCorsDefaults(HttpEndpointOptions options, IReadOnlyDictionary<string, string> rawParams)
    {
        if (!rawParams.ContainsKey("cors"))
            options.Cors = DefaultCors.Enabled;

        if (!rawParams.ContainsKey("corsOrigins") && DefaultCors.Origins is not null)
            options.CorsOrigins ??= DefaultCors.Origins;

        if (!rawParams.ContainsKey("corsCredentials"))
            options.CorsCredentials = options.CorsCredentials || DefaultCors.Credentials;

        // Resolver delegate cannot ride in URI parameters (it is a Func, not a string),
        // so we propagate the global default unless the endpoint already set its own.
        // BUT: when the URI declared its own static whitelist (corsOrigins=...), the caller
        // is making an explicit "this exact origin policy" statement -- injecting a
        // component-wide resolver on top would override that intent silently. So we only
        // inject the global resolver when the endpoint did not pin its own origins.
        if (!rawParams.ContainsKey("corsOrigins"))
            options.CorsOriginsResolver ??= DefaultCors.OriginsResolver;
    }
}

/// <summary>
/// HTTPS component. Behaves identically to HTTP but uses the "https" scheme.
/// </summary>
public class HttpsComponent : HttpComponent
{
    /// <summary>Creates an HTTPS component.</summary>
    public HttpsComponent() : base("https") { }
}

/// <summary>
/// HTTP endpoint. Creates either a producer (HTTP client) or consumer (Kestrel server).
/// <para>The path part of the URI is: host[:port]/path</para>
/// <para>For producer: resolves to target URL.</para>
/// <para>For consumer: path is the route to listen on.</para>
/// </summary>
public class HttpEndpoint : EndpointBase<HttpEndpointOptions>
{
    private readonly string _cleanPath;
    private readonly bool _hasMethodPrefix;

    /// <summary>Creates an HTTP endpoint.</summary>
    /// <param name="uri">Original parsed URI.</param>
    /// <param name="component">Parent HTTP component.</param>
    /// <param name="options">Parsed endpoint options.</param>
    /// <param name="cleanPath">Path with method prefix stripped (if any). Falls back to uri.Path.</param>
    /// <param name="hasMethodPrefix">Whether a method prefix was stripped from the path.</param>
    public HttpEndpoint(EndpointUri uri, HttpComponent component, HttpEndpointOptions options,
        string? cleanPath = null, bool hasMethodPrefix = false)
        : base(uri, component, options)
    {
        _cleanPath = cleanPath ?? uri.Path;
        _hasMethodPrefix = hasMethodPrefix;
    }

    /// <summary>Whether this endpoint uses HTTPS.</summary>
    public bool IsHttps => Uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase);

    /// <summary>The scheme prefix for building URLs (http:// or https://).</summary>
    public string SchemePrefix => IsHttps ? "https://" : "http://";

    /// <summary>The endpoint options for external access.</summary>
    internal HttpEndpointOptions EndpointOptions => Options;

    /// <summary>
    /// Build the full target URL for the producer.
    /// URI path is "host:port/path" or "host/path" — we prepend scheme.
    /// </summary>
    public string BuildProducerUrl()
    {
        var path = _cleanPath.TrimStart('/');
        return $"{SchemePrefix}{path}";
    }

    /// <summary>
    /// The consumer listen path (the route portion after host:port).
    /// For "http:0.0.0.0:9090/webhook", returns "/webhook".
    /// For "http:0.0.0.0:9090", returns "/".
    /// For "http:GET:/webhook", returns "/webhook".
    /// </summary>
    public string ConsumerPath
    {
        get
        {
            var path = _cleanPath;

            // When method prefix was used and path starts with '/',
            // it IS the route directly (e.g. "GET:/connect/token" → "/connect/token").
            // If it doesn't start with '/', host:port is still present (e.g. "POST:0.0.0.0:8080/api").
            if (_hasMethodPrefix && path.StartsWith('/'))
                return path;

            // Path format: host:port/route or host/route
            if (path.StartsWith('/'))
            {
                var rest = path[1..]; // host:port/route
                var restSlash = rest.IndexOf('/');
                return restSlash >= 0 ? rest[restSlash..] : "/";
            }

            var idx = path.IndexOf('/');
            return idx >= 0 ? path[idx..] : "/";
        }
    }

    /// <inheritdoc />
    public override IProducer CreateProducer()
    {
        return new HttpProducer(this, Options);
    }

    /// <inheritdoc />
    public override IConsumer CreateConsumer(IProcessor processor)
    {
        ArgumentNullException.ThrowIfNull(processor);

        var component = (HttpComponent)Component;
        var serverManager = component.ServerManager
            ?? throw new InvalidOperationException(
                "SharedHttpServerManager is not configured. " +
                "Register HTTP via AddRedbRouteHttp() in DI.");

        return new HttpConsumer(this, processor, Options, serverManager);
    }
}

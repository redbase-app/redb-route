using redb.Route.Core;

namespace redb.Route.Http;

/// <summary>
/// Options for the HTTP endpoint. Shared by both producer and consumer.
/// Use URI parameters to configure: http:host:port/path?method=POST&amp;timeout=30000
/// </summary>
public class HttpEndpointOptions : EndpointOptions
{
    // ── Common ──────────────────────────────────────────

    /// <summary>HTTP method. Default: GET for producer, all methods accepted for consumer.</summary>
    public HttpMethod Method { get; set; } = HttpMethod.GET;

    /// <summary>Request/response timeout in milliseconds. Default: 30000 (30s). Producer only.</summary>
    public int Timeout { get; set; } = 30_000;

    /// <summary>Content-Type header value for outgoing requests/responses. Default: application/json.</summary>
    public string ContentType { get; set; } = "application/json";

    // ── Producer (HttpClient) ───────────────────────────

    /// <summary>Throw an exception on HTTP 4xx/5xx responses. Default: true.</summary>
    public bool ThrowOnError { get; set; } = true;

    /// <summary>Bridge exchange headers to HTTP request headers. Default: true.</summary>
    public bool BridgeHeaders { get; set; } = true;

    /// <summary>Authentication scheme. Default: None.</summary>
    public HttpAuthScheme AuthScheme { get; set; } = HttpAuthScheme.None;

    /// <summary>Username for Basic auth.</summary>
    public string? Username { get; set; }

    /// <summary>Password for Basic auth.</summary>
    [Sensitive]
    public string? Password { get; set; }

    /// <summary>Bearer token value (or expression) for Bearer auth.</summary>
    [Sensitive]
    public DynamicValue<string>? AuthToken { get; set; }

    /// <summary>
    /// Named <see cref="HttpConnectionFactory"/> from the route registry. Lets Basic/Bearer
    /// credentials and the TLS certificate password live in the registry instead of the endpoint
    /// URI, so they never reach logs or dashboards.
    /// </summary>
    public string? ConnectionFactory { get; set; }

    /// <summary>Follow HTTP redirects. Default: true.</summary>
    public bool FollowRedirects { get; set; } = true;

    /// <summary>Maximum number of automatic redirects. Default: 50.</summary>
    public int MaxRedirects { get; set; } = 50;

    /// <summary>Copy HTTP response headers back to exchange headers. Default: true.</summary>
    public bool CopyResponseHeaders { get; set; } = true;

    /// <summary>Preserve the Host header from the original request. Default: false.</summary>
    public bool PreserveHostHeader { get; set; }

    // ── Consumer (Kestrel) ──────────────────────────────

    /// <summary>Bind host for the embedded HTTP server. Default: 0.0.0.0.</summary>
    public string Host { get; set; } = "0.0.0.0";

    /// <summary>Bind port for the embedded HTTP server. Default: 8080.</summary>
    public int Port { get; set; } = 8080;

    /// <summary>
    /// Comma-separated list of allowed HTTP methods for the consumer.
    /// Default: empty (all methods accepted).
    /// Example: "POST,PUT"
    /// </summary>
    public string? Methods { get; set; }

    /// <summary>Enable CORS for all origins. Default: false.</summary>
    public bool Cors { get; set; }

    /// <summary>
    /// CORS allowed origins (comma-separated). Default: * when CORS is enabled.
    /// Example: "https://example.com,https://app.example.com"
    /// <para>NOTE: browsers do NOT understand a comma-separated <c>Access-Control-Allow-Origin</c>
    /// header. When this property contains multiple origins, the consumer middleware will echo
    /// back the request's <c>Origin</c> header IF it appears in the list, otherwise the request
    /// is treated as not allowed (no CORS headers emitted, browser preflight fails).
    /// For multi-origin scenarios prefer <see cref="CorsOriginsResolver"/> for full control.</para>
    /// </summary>
    public string? CorsOrigins { get; set; }

    /// <summary>
    /// Optional per-request CORS-origin resolver. When set, takes precedence over
    /// <see cref="CorsOrigins"/>. The delegate receives the incoming <see cref="Microsoft.AspNetCore.Http.HttpRequest"/>
    /// and returns either:
    /// <list type="bullet">
    ///   <item>a specific origin string (e.g. <c>"https://app.example.com"</c>) — echoed back
    ///         in <c>Access-Control-Allow-Origin</c>;</item>
    ///   <item><c>"*"</c> — wildcard (browsers reject this combination with credentials);</item>
    ///   <item><c>null</c> — origin not allowed; no CORS headers will be emitted.</item>
    /// </list>
    /// The delegate must be cheap and thread-safe; it runs on every CORS-eligible request.
    /// Cannot be configured via URI parameters; set this in DI via the global
    /// <see cref="HttpCorsOptions.OriginsResolver"/> or programmatically on the endpoint options.
    /// </summary>
    public Func<Microsoft.AspNetCore.Http.HttpRequest, string?>? CorsOriginsResolver { get; set; }

    /// <summary>
    /// Enable Access-Control-Allow-Credentials header. Default: false.
    /// When true, the resolved origin must be a specific value (not <c>*</c>) — browsers reject
    /// the combination. The runtime middleware enforces this regardless of how the origin was
    /// resolved (static <see cref="CorsOrigins"/> or <see cref="CorsOriginsResolver"/>).
    /// </summary>
    public bool CorsCredentials { get; set; }

    /// <summary>Maximum request body size in bytes. Default: 10 MB (10485760). 0 = unlimited.</summary>
    public long MaxRequestBodySize { get; set; } = 10 * 1024 * 1024;

    /// <summary>HTTP protocol version for the consumer. Default: Http1And2.</summary>
    public HttpProtocol Protocol { get; set; } = HttpProtocol.Http1And2;

    /// <summary>Enable HTTPS for the consumer. Default: false.</summary>
    public bool Ssl { get; set; }

    /// <summary>Path to PFX certificate file for HTTPS. Required when ssl=true.</summary>
    public string? SslCertPath { get; set; }

    /// <summary>Password for the PFX certificate.</summary>
    [Sensitive]
    public string? SslCertPassword { get; set; }

    /// <summary>
    /// Response status code for successful processing. Default: 200.
    /// </summary>
    public int ResponseCode { get; set; } = 200;

    /// <summary>
    /// If true, the consumer returns the exchange Out body as the HTTP response (InOut pattern).
    /// If false, returns an empty 200 OK (InOnly pattern). Default: false.
    /// </summary>
    public bool InOut { get; set; }

    /// <summary>
    /// If true, enables streaming of request body instead of buffering.
    /// Useful for large payloads. Default: false.
    /// </summary>
    public bool StreamRequest { get; set; }

    /// <summary>
    /// If true, the producer sets the exchange Out body to a Stream instead of byte[].
    /// The stream is closed automatically by <c>Exchange.DisposeAsync()</c>. Default: false.
    /// </summary>
    public bool StreamResponse { get; set; }

    // ── Named Parameters ────────────────────────────────────────────

    private IReadOnlyDictionary<string, string>? _explicitParameters;

    /// <summary>
    /// Named parameter bindings from <c>.Param()</c> calls.
    /// Extracted from URI parameters with <c>param.</c> prefix.
    /// Used to resolve <c>{name}</c> placeholders in the URL path at runtime.
    /// Values may contain <c>${...}</c> expressions resolved before substitution.
    /// </summary>
    internal IReadOnlyDictionary<string, string> ExplicitParameters =>
        _explicitParameters ??= ParseExplicitParameters();

    private IReadOnlyDictionary<string, string> ParseExplicitParameters()
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in UnmappedParameters)
        {
            if (key.StartsWith("param.", StringComparison.OrdinalIgnoreCase))
                dict[key["param.".Length..]] = value;
        }
        return dict;
    }

    /// <inheritdoc />
    public override void Validate()
    {
        if (Timeout < 0)
            throw new ArgumentException("Timeout must be >= 0.");

        if (Port is < 0 or > 65535)
            throw new ArgumentException("Port must be between 0 and 65535.");

        if (MaxRedirects < 0)
            throw new ArgumentException("MaxRedirects must be >= 0.");

        if (MaxRequestBodySize < 0)
            throw new ArgumentException("MaxRequestBodySize must be >= 0.");

        if (ResponseCode is < 100 or > 599)
            throw new ArgumentException("ResponseCode must be between 100 and 599.");

        if (AuthScheme == HttpAuthScheme.Basic && (string.IsNullOrEmpty(Username) || string.IsNullOrEmpty(Password)))
            throw new ArgumentException("Username and Password are required when AuthScheme=Basic.");

        if (Ssl && string.IsNullOrEmpty(SslCertPath))
            throw new ArgumentException("SslCertPath is required when Ssl=true.");

        // CORS configuration must be explicit. When Cors=true, the caller MUST supply either
        // a static whitelist (CorsOrigins, including "*" for public endpoints) or a resolver
        // (CorsOriginsResolver). The legacy implicit "*" fallback is a footgun that browsers
        // silently downgrade in combination with credentials, so we surface the misconfiguration
        // at startup instead.
        if (Cors && string.IsNullOrEmpty(CorsOrigins) && CorsOriginsResolver is null)
            throw new ArgumentException(
                "Cors=true requires CorsOrigins (use \"*\" for public endpoints) or CorsOriginsResolver to be set.");

        // CorsCredentials + wildcard guard: only enforceable at config time when no resolver
        // is set. With a resolver, the same constraint is enforced per-request inside the
        // CORS middleware (see HttpConsumer.Start) — the resolver may legitimately return a
        // specific origin even when CorsOrigins is null/empty.
        if (CorsCredentials && CorsOriginsResolver is null && string.IsNullOrEmpty(CorsOrigins))
            throw new ArgumentException("CorsOrigins or CorsOriginsResolver must be set when CorsCredentials=true (wildcard * is not allowed by browsers in combination with credentials).");
    }
}

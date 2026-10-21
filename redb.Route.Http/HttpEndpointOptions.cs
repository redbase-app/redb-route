using redb.Route.Core;

namespace redb.Route.Http;

/// <summary>
/// Options for the HTTP endpoint. Shared by both producer and consumer.
/// Use URI parameters to configure: http:host:port/path?method=POST&amp;timeout=30000
/// </summary>
[LenientProperties]
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

    /// <summary>
    /// Do not map the exchange headers to request headers at all; only the fields the producer sets itself go out.
    /// Default: false. Apache Camel's <c>skipRequestHeaders</c>.
    /// </summary>
    [EndpointRole(EndpointRole.Producer)]
    public bool SkipRequestHeaders { get; set; }

    /// <summary>
    /// Do not let the control headers on the exchange — <c>redbHttp.Method</c>, <c>redbHttp.Query</c> — steer the
    /// request; the endpoint's own options decide. Default: false. Set it on a producer whose exchange may carry
    /// headers of other hops (a message received from a broker keeps every header it came with). Apache Camel's
    /// <c>skipControlHeaders</c>.
    /// </summary>
    [EndpointRole(EndpointRole.Producer)]
    public bool SkipControlHeaders { get; set; }

    /// <summary>
    /// A registry reference (<c>#name</c>) to an <see cref="redb.Route.Abstractions.IHeaderFilterStrategy"/> that decides
    /// which headers this endpoint carries, in place of <see cref="HttpHeaderFilterStrategy"/>: the request and
    /// response headers of a producer, the request and response headers of a consumer. The protocol fields
    /// (<see cref="HttpHeaders.ProtocolHeaders"/>) and, on a consumer, received <c>redbHttp.*</c> and response-only
    /// fields are never carried, whatever the strategy says.
    /// </summary>
    public string? HeaderFilterStrategy { get; set; }

    /// <summary>Authentication scheme. Default: None.</summary>
    [EndpointRole(EndpointRole.Producer)]
    public HttpAuthScheme AuthScheme { get; set; } = HttpAuthScheme.None;

    /// <summary>Username for Basic auth.</summary>
    [EndpointRole(EndpointRole.Producer)]
    public string? Username { get; set; }

    /// <summary>Password for Basic auth.</summary>
    [Sensitive]
    [EndpointRole(EndpointRole.Producer)]
    public string? Password { get; set; }

    /// <summary>Bearer token value (or expression) for Bearer auth.</summary>
    [Sensitive]
    [EndpointRole(EndpointRole.Producer)]
    public DynamicValue<string>? AuthToken { get; set; }

    // ── Inbound authentication (consumer only) ──

    /// <summary>
    /// Consumer only: how an inbound request proves who it is. <c>Basic</c> checks the <c>Authorization</c> header
    /// against <see cref="InboundUsername"/> / <see cref="InboundPassword"/>; <c>Bearer</c> hands the token to the
    /// <see cref="IHttpTokenValidator"/> named by <see cref="TokenValidator"/>. A refused request gets 401 with
    /// <c>WWW-Authenticate</c> and never reaches the route. Default <c>None</c>.
    /// </summary>
    [EndpointRole(EndpointRole.Consumer)]
    public HttpAuthScheme InboundAuth { get; set; } = HttpAuthScheme.None;

    /// <summary>Consumer only: the user name <c>inboundAuth=basic</c> accepts.</summary>
    [EndpointRole(EndpointRole.Consumer)]
    public string? InboundUsername { get; set; }

    /// <summary>Consumer only: the password <c>inboundAuth=basic</c> accepts. Take it from configuration (<c>{{…}}</c>).</summary>
    [Sensitive]
    [EndpointRole(EndpointRole.Consumer)]
    public string? InboundPassword { get; set; }

    /// <summary>Consumer only: the realm named in <c>WWW-Authenticate</c>. Default <c>redb</c>.</summary>
    [EndpointRole(EndpointRole.Consumer)]
    public string InboundRealm { get; set; } = "redb";

    /// <summary>Consumer only: registry name of the <see cref="IHttpTokenValidator"/> for <c>inboundAuth=bearer</c>, <c>#name</c> or <c>name</c>.</summary>
    [EndpointRole(EndpointRole.Consumer)]
    public string? TokenValidator { get; set; }

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

    /// <summary>
    /// Do not map the response headers to exchange headers; the status, URL and content type are still set.
    /// Default: false. Apache Camel's <c>skipResponseHeaders</c>.
    /// </summary>
    [EndpointRole(EndpointRole.Producer)]
    public bool SkipResponseHeaders { get; set; }

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

    // ── Admission limit (HTTP_CONCURRENCY_LIMITS_PLAN) ──

    /// <summary>
    /// Maximum concurrent executions of this consumer's pipeline. 0 (default) = unlimited —
    /// Kestrel runs as many as requests arrive. Overflow beyond the limit and
    /// <see cref="RequestQueueLimit"/> is shed with <see cref="RejectStatusCode"/> before any
    /// pipeline work (load shedding, not backpressure).
    /// </summary>
    public int MaxConcurrentRequests { get; set; }

    /// <summary>
    /// How many requests over the limit WAIT for a permit (FIFO) instead of being rejected.
    /// 0 (default) = reject immediately once the permits are taken.
    /// </summary>
    public int RequestQueueLimit { get; set; }

    /// <summary>Status code for a shed request. Default 429 Too Many Requests.</summary>
    public int RejectStatusCode { get; set; } = 429;

    /// <summary>Value of the <c>Retry-After</c> header on a shed request; 0 = do not send it. Default 1.</summary>
    public int RetryAfterSeconds { get; set; } = 1;

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

    /// <summary>Inbound authentication must be complete and unambiguous: a half-declared check would look closed and be open.</summary>
    private void ValidateInboundAuth()
    {
        switch (InboundAuth)
        {
            case HttpAuthScheme.None:
                if (InboundUsername is not null || InboundPassword is not null || TokenValidator is not null)
                    throw new ArgumentException(
                        "inboundUsername, inboundPassword and tokenValidator take effect only with inboundAuth=basic or " +
                        "inboundAuth=bearer; without it the endpoint would accept every caller while looking protected.");
                break;
            case HttpAuthScheme.Basic:
                if (string.IsNullOrEmpty(InboundUsername) || string.IsNullOrEmpty(InboundPassword))
                    throw new ArgumentException("inboundAuth=basic needs inboundUsername and inboundPassword.");
                if (TokenValidator is not null)
                    throw new ArgumentException("tokenValidator checks bearer tokens; inboundAuth=basic does not use it.");
                break;
            case HttpAuthScheme.Bearer:
                if (string.IsNullOrWhiteSpace(TokenValidator))
                    throw new ArgumentException("inboundAuth=bearer needs tokenValidator=#name, a registered IHttpTokenValidator.");
                if (InboundUsername is not null || InboundPassword is not null)
                    throw new ArgumentException("inboundUsername and inboundPassword belong to inboundAuth=basic.");
                break;
        }

        // The realm is written into a quoted-string of WWW-Authenticate.
        if (string.IsNullOrEmpty(InboundRealm) || InboundRealm.Any(c => c is '"' or '\\' || char.IsControl(c)))
            throw new ArgumentException("inboundRealm must be non-empty, without quotes, backslashes or control characters.");
    }

    /// <summary>
    /// Options renamed to Apache Camel's names, with the meaning reversed. The HTTP options take parameters they have
    /// no property for (<see cref="LenientPropertiesAttribute"/>), so an old name would be dropped without a word and
    /// its endpoint would quietly do the opposite of what it says: it is refused instead.
    /// </summary>
    private static readonly Dictionary<string, string> RenamedOptions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["bridgeHeaders"] = "'bridgeHeaders' is replaced by Apache Camel's 'skipRequestHeaders', with the meaning reversed: " +
                            "bridgeHeaders=false is skipRequestHeaders=true; bridgeHeaders=true is the default and needs no option.",
        ["copyResponseHeaders"] = "'copyResponseHeaders' is replaced by Apache Camel's 'skipResponseHeaders', with the meaning " +
                                  "reversed: copyResponseHeaders=false is skipResponseHeaders=true; copyResponseHeaders=true " +
                                  "is the default and needs no option.",
    };

    /// <inheritdoc />
    public override void Validate()
    {
        foreach (var key in UnmappedParameters.Keys)
        {
            if (RenamedOptions.TryGetValue(key, out var replacement))
                throw new ArgumentException(replacement, key);
        }

        if (Timeout < 0)
            throw new ArgumentException("Timeout must be >= 0.");

        if (Port is < 0 or > 65535)
            throw new ArgumentException("Port must be between 0 and 65535.");

        if (MaxRedirects < 0)
            throw new ArgumentException("MaxRedirects must be >= 0.");

        if (MaxRequestBodySize < 0)
            throw new ArgumentException("MaxRequestBodySize must be >= 0.");

        ConcurrencyLimitOptions.ValidateShape(MaxConcurrentRequests, RequestQueueLimit, RejectStatusCode, RetryAfterSeconds);

        if (ResponseCode is < 100 or > 599)
            throw new ArgumentException("ResponseCode must be between 100 and 599.");

        if (AuthScheme == HttpAuthScheme.Basic && (string.IsNullOrEmpty(Username) || string.IsNullOrEmpty(Password)))
            throw new ArgumentException("Username and Password are required when AuthScheme=Basic.");

        ValidateInboundAuth();

        // No SslCertPath check here on purpose. The certificate may legitimately come from a named
        // connection factory or from the host default (HttpHostingOptions.Tls), the way a Camel
        // endpoint resolves from global SSLContextParameters. The invariant "TLS asked for and no
        // certificate resolves" is enforced once, at the bind, by SharedHttpServerManager — the
        // only place that can see all three sources.

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

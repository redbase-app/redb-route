namespace redb.Route.Http;

/// <summary>
/// Well-known header constants used by the HTTP component.
/// Exchange headers with the "redbHttp." prefix are set/read automatically.
/// Standard HTTP headers (Content-Type, Authorization, etc.) are bridged as-is.
/// </summary>
public static class HttpHeaders
{
    /// <summary>Common prefix for all HTTP component headers.</summary>
    public const string Prefix = "redbHttp.";

    /// <summary>HTTP response status code (int). Set by producer after response, by consumer in Out message.</summary>
    public const string StatusCode = "redbHttp.StatusCode";

    /// <summary>HTTP response reason phrase.</summary>
    public const string StatusText = "redbHttp.StatusText";

    /// <summary>The full request/response URL.</summary>
    public const string Url = "redbHttp.Url";

    /// <summary>HTTP method used (GET, POST, etc.).</summary>
    public const string Method = "redbHttp.Method";

    /// <summary>HTTP Content-Type header value.</summary>
    public const string ContentType = "Content-Type";

    /// <summary>HTTP Content-Length header value.</summary>
    public const string ContentLength = "Content-Length";

    /// <summary>Query string from the incoming request (consumer). Example: "param1=val1&amp;param2=val2".</summary>
    public const string Query = "redbHttp.Query";

    /// <summary>Raw request path (consumer). Example: "/webhook".</summary>
    public const string Path = "redbHttp.Path";

    /// <summary>Remote IP address of the client (consumer).</summary>
    public const string RemoteAddress = "redbHttp.RemoteAddress";

    /// <summary>Server port the request was received on (consumer).</summary>
    public const string Port = "redbHttp.Port";

    /// <summary>Response content type override. If set, producer/consumer use this for response Content-Type.</summary>
    public const string ResponseContentType = "redbHttp.ResponseContentType";

    /// <summary>Response status code override. If set as int, consumer uses this for response status code.</summary>
    public const string ResponseCode = "redbHttp.ResponseCode";

    /// <summary>Authorization header value.</summary>
    public const string Authorization = "Authorization";

    /// <summary>Accept header value.</summary>
    public const string Accept = "Accept";

    /// <summary>User-Agent header value.</summary>
    public const string UserAgent = "User-Agent";

    /// <summary>Prefix for route template parameters. Example: "redbHttp.RouteParam.id".</summary>
    public const string RouteParamPrefix = "redbHttp.RouteParam.";

    /// <summary>Prefix for query string parameters. Example: "redbHttp.QueryParam.sort".</summary>
    public const string QueryParamPrefix = "redbHttp.QueryParam.";

    // ── What is never carried, and what the default strategy keeps back ──

    /// <summary>
    /// The control headers of this component — the request it builds and the exchange a consumer fills. The default
    /// strategy of a producer (<see cref="HttpHeaderFilterStrategy"/>) does not send them as request headers; a
    /// consumer never takes a received <c>redbHttp.*</c> header and never answers with one.
    /// </summary>
    public static readonly IReadOnlySet<string> ControlHeaders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        StatusCode, StatusText, Url, Method, Query, Path, RemoteAddress, Port,
        ResponseContentType, ResponseCode,
    };

    /// <summary>
    /// Fields of the protocol, never carried between an exchange and a message whatever the header filter strategy
    /// says: the hop-by-hop fields, <c>Host</c> (only <c>preserveHostHeader=true</c> carries the original one) and the
    /// content fields, which describe the body and are set from it.
    /// </summary>
    public static readonly IReadOnlySet<string> ProtocolHeaders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        // Hop-by-hop HTTP headers
        "Connection", "Keep-Alive", "Transfer-Encoding", "TE",
        "Trailer", "Upgrade", "Proxy-Authorization", "Proxy-Authenticate",
        // Host names THIS hop's target. Bridging it verbatim turned every proxy route into an
        // accidental preserve-host proxy; the explicit preserveHostHeader=true is the one way
        // to carry the original through (часть B плана KAFKA_HARDENING_AND_OPTIONS_SWEEP_PLAN).
        "Host",
        // Content headers managed by HttpClient
        ContentType, ContentLength, "Content-Encoding", "Content-Language",
        "Content-Disposition", "Content-Range"
    };

    /// <summary>
    /// Fields only a response carries (RFC 9110 §10.2 and §11, RFC 6265 <c>Set-Cookie</c>). A consumer drops them from an
    /// inbound request before the exchange is built, so neither the route nor the response ever holds a value a client
    /// planted under a response field's name. Stricter than Camel's inbound filter, which removes only its own
    /// <c>Camel*</c> headers: a request has no legitimate use for these, and one that sends them is probing.
    /// </summary>
    public static readonly IReadOnlySet<string> ResponseOnlyHeaders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Set-Cookie", "Location", "WWW-Authenticate", "Proxy-Authenticate", "Authentication-Info",
        "Proxy-Authentication-Info", "Retry-After", "Server", "Age", "ETag", "Accept-Ranges", "Vary",
    };

    /// <summary>Returns true if the header key belongs to the HTTP component (starts with "redbHttp.").</summary>
    public static bool IsRedbHeader(string key) =>
        key.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);
}

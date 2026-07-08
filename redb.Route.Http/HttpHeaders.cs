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

    // ── Standard HTTP headers that are bridged when BridgeHeaders=true ──

    /// <summary>Set of header names that are NOT bridged from exchange to HTTP request (internal/hop-by-hop).</summary>
    internal static readonly HashSet<string> NonBridgedHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        // Internal redb headers
        StatusCode, StatusText, Url, Method, Query, Path, RemoteAddress, Port,
        ResponseContentType, ResponseCode,
        // Hop-by-hop HTTP headers
        "Connection", "Keep-Alive", "Transfer-Encoding", "TE",
        "Trailer", "Upgrade", "Proxy-Authorization", "Proxy-Authenticate",
        // Content headers managed by HttpClient
        ContentType, ContentLength, "Content-Encoding", "Content-Language",
        "Content-Disposition", "Content-Range"
    };

    /// <summary>Returns true if the header key belongs to the HTTP component (starts with "redbHttp.").</summary>
    public static bool IsRedbHeader(string key) =>
        key.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);
}

namespace redb.Route.Http;

/// <summary>
/// HTTP methods supported by the component.
/// </summary>
public enum HttpMethod
{
    /// <summary>HTTP GET.</summary>
    GET,
    /// <summary>HTTP POST.</summary>
    POST,
    /// <summary>HTTP PUT.</summary>
    PUT,
    /// <summary>HTTP DELETE.</summary>
    DELETE,
    /// <summary>HTTP PATCH.</summary>
    PATCH,
    /// <summary>HTTP HEAD.</summary>
    HEAD,
    /// <summary>HTTP OPTIONS.</summary>
    OPTIONS
}

/// <summary>
/// HTTP protocol version for Kestrel consumer.
/// </summary>
public enum HttpProtocol
{
    /// <summary>HTTP/1.1 only.</summary>
    Http1,
    /// <summary>HTTP/2 only. Requires HTTPS on most clients.</summary>
    Http2,
    /// <summary>HTTP/1.1 and HTTP/2 (default). Negotiated via ALPN.</summary>
    Http1And2,
    /// <summary>HTTP/3 only (QUIC). Requires HTTPS.</summary>
    Http3,
    /// <summary>HTTP/1.1, HTTP/2, and HTTP/3.</summary>
    Http1And2And3
}

/// <summary>
/// Authentication scheme for producer HTTP requests.
/// </summary>
public enum HttpAuthScheme
{
    /// <summary>No authentication.</summary>
    None,
    /// <summary>HTTP Basic authentication (username:password).</summary>
    Basic,
    /// <summary>Bearer token authentication.</summary>
    Bearer
}

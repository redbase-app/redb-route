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

// HttpProtocol moved to redb.Route.Http.Hosting (kept in the redb.Route.Http namespace) — it is shared
// between the Kestrel host and the HTTP connector options.

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

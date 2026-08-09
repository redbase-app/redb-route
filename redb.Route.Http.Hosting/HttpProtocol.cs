namespace redb.Route.Http;

/// <summary>
/// HTTP protocol version for the Kestrel host. Lives in the shared hosting assembly (kept in the
/// <c>redb.Route.Http</c> namespace for source compatibility) because both the server manager and the
/// HTTP connector's options reference it.
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

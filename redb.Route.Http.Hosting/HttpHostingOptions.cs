using System.Security.Claims;
using Microsoft.AspNetCore.Http;

namespace redb.Route.Http;

/// <summary>
/// Process-wide settings of the shared Kestrel host. One instance serves every listener the
/// <see cref="SharedHttpServerManager"/> opens, because the things configured here (which proxies
/// to trust) describe the deployment the process runs in, not any single route.
/// </summary>
/// <example>
/// <code>
/// services.AddRedbRouteHttpHosting(o => o.TrustedProxies.Add("10.0.0.5").Add("10.1.0.0/16"));
/// </code>
/// </example>
public sealed class HttpHostingOptions
{
    /// <summary>Reverse proxies allowed to set the client address and scheme. Empty by default.</summary>
    public TrustedProxyOptions TrustedProxies { get; } = new();

    /// <summary>Server-wide TLS material, used by listeners whose endpoint does not carry its own.</summary>
    public ServerTlsOptions Tls { get; } = new();

    /// <summary>Kestrel connection ceilings applied to every listener the manager opens.</summary>
    public HostConnectionLimits Limits { get; } = new();

    /// <summary>
    /// Identifies the caller of each request on every listener the manager opens. Null (the default)
    /// leaves requests unidentified and the host byte-for-byte what it was.
    /// <para>
    /// It runs once per request: after trusted-proxy resolution, so it sees the client's real address
    /// and scheme; after CORS, so a preflight never reaches it; and before any transport sees the
    /// request. A non-null result is kept in <c>HttpContext.Items</c> under
    /// <see cref="SharedHttpServerManager.PrincipalItem"/>, and the HTTP, gRPC, SOAP, AS2, WebSocket and
    /// SignalR consumers put it on the exchange (<c>ExchangePrincipal</c>).
    /// </para>
    /// <para>
    /// It identifies; it does not authorize. Return <c>null</c> for an anonymous caller: such a request
    /// is still served, because one listener carries routes with different requirements, and whether
    /// an anonymous caller is acceptable is each route's decision. A resolver that <b>throws</b> fails the
    /// request with 500 and an error log instead — one that could not decide must not quietly hand the
    /// route an anonymous caller that may not be one. Build the identity with an authentication type
    /// (<c>new ClaimsIdentity(claims, "Bearer")</c>): code that reads the principal treats an identity that
    /// is not authenticated as anonymous.
    /// </para>
    /// <para>
    /// A transport's own authenticate hook (<c>WsComponent.Authenticate</c>,
    /// <c>SignalRComponent.Authenticate</c>) takes precedence on that transport's paths.
    /// </para>
    /// </summary>
    /// <example>
    /// <code>
    /// services.AddRedbRouteHttpHosting(o => o.ResolvePrincipal = ctx =>
    ///     myTokenValidator.ValidateAsync(ctx.Request.Headers.Authorization.ToString()));
    /// </code>
    /// </example>
    public Func<HttpContext, Task<ClaimsPrincipal?>>? ResolvePrincipal { get; set; }
}

/// <summary>
/// Coarse host-wide backstop: Kestrel's own connection ceilings, applied to every listener this
/// process opens. Kestrel's defaults are unlimited; these protect the whole port. The fine-grained
/// tool is the per-route admission limit (<see cref="ConcurrencyLimitOptions"/>) — this is the
/// fuse, not the thermostat.
/// </summary>
public sealed class HostConnectionLimits
{
    /// <summary>Maximum concurrent connections per listener. Null (default) = Kestrel default (unlimited).</summary>
    public long? MaxConcurrentConnections { get; set; }

    /// <summary>
    /// Maximum concurrent upgraded connections (WebSockets) per listener.
    /// Null (default) = Kestrel default (unlimited).
    /// </summary>
    public long? MaxConcurrentUpgradedConnections { get; set; }
}

/// <summary>
/// The host's own TLS material. A listener that asks for TLS resolves its certificate from the
/// endpoint first (<c>sslCertPath</c>, or a named connection factory), and falls back to what is
/// set here — the same shape as Camel's global <c>SSLContextParameters</c> and Spring Boot's SSL
/// bundles, where a certificate on the endpoint is an override rather than a requirement.
/// <para>
/// Nothing here turns TLS on: <c>ssl</c> stays an explicit per-endpoint decision. This only
/// answers "with which certificate". A listener that asks for TLS and finds no certificate in
/// either place refuses to bind.
/// </para>
/// </summary>
/// <example>
/// <code>
/// services.AddRedbRouteHttpHosting(o => o.Tls.DefaultCertificatePath = "/certs/server.pfx");
/// </code>
/// </example>
public sealed class ServerTlsOptions
{
    /// <summary>
    /// Certificate served by any TLS listener that has none of its own. Takes precedence over
    /// <see cref="DefaultCertificatePath"/>, which is the convenience form of the same thing.
    /// </summary>
    public System.Security.Cryptography.X509Certificates.X509Certificate2? DefaultCertificate { get; set; }

    /// <summary>Path to a PFX file serving the same role as <see cref="DefaultCertificate"/>.</summary>
    public string? DefaultCertificatePath { get; set; }

    /// <summary>Password for <see cref="DefaultCertificatePath"/>.</summary>
    public string? DefaultCertificatePassword { get; set; }

    /// <summary>True when the host can supply a certificate to a listener that has none.</summary>
    internal bool HasDefault => DefaultCertificate is not null || !string.IsNullOrEmpty(DefaultCertificatePath);
}

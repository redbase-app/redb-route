using redb.Route.Abstractions;

namespace redb.Route.Tcp;

/// <summary>
/// Named connection factory for TCP. Register it in the route registry and reference it by name
/// from the endpoint URI (<c>connectionFactory=my-tcp</c>) so the TLS certificate password never
/// has to appear in the URI, and therefore can never reach logs, telemetry, or the dashboard.
/// <para>
/// The listen/target address stays in the endpoint path — a factory carries TLS material and
/// timeouts only and can never silently redirect a route.
/// </para>
/// </summary>
public sealed class TcpConnectionFactory
{
    /// <summary>Serve/connect over TLS.</summary>
    public bool Ssl { get; set; }

    /// <summary>Path to the PFX certificate.</summary>
    public string? SslCertPath { get; set; }

    /// <summary>Password for the PFX certificate.</summary>
    public string? SslCertPassword { get; set; }

    /// <summary>Expected TLS target host name (SNI / certificate validation).</summary>
    public string? SslTargetHost { get; set; }

    /// <summary>Connect timeout in milliseconds.</summary>
    public int ConnectTimeout { get; set; } = 10_000;

    /// <summary>
    /// Copies this factory's TLS and timeout settings onto the endpoint options, but only for
    /// parameters the endpoint URI did not set explicitly — an inline URI value always wins.
    /// </summary>
    internal void ApplyTo(TcpEndpointOptions options, EndpointUri uri)
    {
        var supplied = uri.RawParameters;

        if (!supplied.ContainsKey(nameof(options.Ssl))) options.Ssl = Ssl;
        if (!supplied.ContainsKey(nameof(options.SslCertPath))) options.SslCertPath = SslCertPath;
        if (!supplied.ContainsKey(nameof(options.SslCertPassword)))
            options.SslCertPassword = SslCertPassword;
        if (!supplied.ContainsKey(nameof(options.SslTargetHost)))
            options.SslTargetHost = SslTargetHost;
        if (!supplied.ContainsKey(nameof(options.ConnectTimeout)))
            options.ConnectTimeout = ConnectTimeout;
    }
}

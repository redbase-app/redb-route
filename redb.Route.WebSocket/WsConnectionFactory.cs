using redb.Route.Abstractions;

namespace redb.Route.WebSocket;

/// <summary>
/// Named connection factory for WebSocket. Register it in the route registry and reference it by
/// name from the endpoint URI (<c>connectionFactory=my-ws</c>) so the TLS certificate password
/// never has to appear in the URI, and therefore can never reach logs, telemetry, or the dashboard.
/// <para>
/// The listen/target address stays in the endpoint path — a factory carries TLS material and
/// timeouts only and can never silently redirect a route. Note the <c>wss</c> scheme still forces
/// TLS on regardless of what the factory says.
/// </para>
/// </summary>
public sealed class WsConnectionFactory
{
    /// <summary>Serve/connect over TLS.</summary>
    public bool Ssl { get; set; }

    /// <summary>Path to the PFX certificate.</summary>
    public string? SslCertPath { get; set; }

    /// <summary>Password for the PFX certificate.</summary>
    public string? SslCertPassword { get; set; }

    /// <summary>Connect timeout in milliseconds.</summary>
    public int ConnectTimeout { get; set; } = 10_000;

    /// <summary>
    /// Copies this factory's TLS and timeout settings onto the endpoint options, but only for
    /// parameters the endpoint URI did not set explicitly — an inline URI value always wins.
    /// </summary>
    internal void ApplyTo(WsEndpointOptions options, EndpointUri uri)
    {
        var supplied = uri.RawParameters;

        if (!supplied.ContainsKey(nameof(options.Ssl))) options.Ssl = Ssl;
        if (!supplied.ContainsKey(nameof(options.SslCertPath))) options.SslCertPath = SslCertPath;
        if (!supplied.ContainsKey(nameof(options.SslCertPassword)))
            options.SslCertPassword = SslCertPassword;
        if (!supplied.ContainsKey(nameof(options.ConnectTimeout)))
            options.ConnectTimeout = ConnectTimeout;
    }
}

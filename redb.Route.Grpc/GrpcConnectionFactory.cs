using redb.Route.Abstractions;

namespace redb.Route.Grpc;

/// <summary>
/// Named connection factory for gRPC. Register it in the route registry and reference it by name
/// from the endpoint URI (<c>connectionFactory=my-grpc</c>) so the TLS certificate password never
/// has to appear in the URI, and therefore can never reach logs, telemetry, or the dashboard.
/// <para>
/// The listen/target address stays in the endpoint path — a factory carries TLS material only and
/// can never silently redirect a route.
/// </para>
/// </summary>
public sealed class GrpcConnectionFactory
{
    /// <summary>Serve/connect over TLS.</summary>
    public bool Ssl { get; set; }

    /// <summary>Path to the PFX certificate.</summary>
    public string? SslCertPath { get; set; }

    /// <summary>Password for the PFX certificate.</summary>
    public string? SslCertPassword { get; set; }

    /// <summary>
    /// Copies this factory's TLS settings onto the endpoint options, but only for parameters the
    /// endpoint URI did not set explicitly — an inline URI value always wins.
    /// </summary>
    internal void ApplyTo(GrpcEndpointOptions options, EndpointUri uri)
    {
        var supplied = uri.RawParameters;

        if (!supplied.ContainsKey(nameof(options.Ssl))) options.Ssl = Ssl;
        if (!supplied.ContainsKey(nameof(options.SslCertPath))) options.SslCertPath = SslCertPath;
        if (!supplied.ContainsKey(nameof(options.SslCertPassword)))
            options.SslCertPassword = SslCertPassword;
    }
}

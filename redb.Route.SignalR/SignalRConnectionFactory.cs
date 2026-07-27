using redb.Route.Abstractions;

namespace redb.Route.SignalR;

/// <summary>
/// Named connection factory for SignalR. Register it in the route registry and reference it by
/// name from the endpoint URI (<c>connectionFactory=my-hub</c>) so the access token and TLS
/// certificate password never have to appear in the URI, and therefore can never reach logs,
/// telemetry, or the dashboard.
/// <para>
/// The hub address stays in the endpoint path — a factory carries credentials and TLS material
/// only and can never silently redirect a route.
/// </para>
/// </summary>
public sealed class SignalRConnectionFactory
{
    /// <summary>Bearer access token for hub authentication.</summary>
    public string? AccessToken { get; set; }

    /// <summary>Transport to use (WebSockets, ServerSentEvents, LongPolling).</summary>
    public SignalRTransport Transport { get; set; } = SignalRTransport.WebSockets;

    /// <summary>Serve/connect over TLS.</summary>
    public bool Ssl { get; set; }

    /// <summary>Path to the PFX certificate.</summary>
    public string? SslCertPath { get; set; }

    /// <summary>Password for the PFX certificate.</summary>
    public string? SslCertPassword { get; set; }

    /// <summary>
    /// Copies this factory's credential and TLS settings onto the endpoint options, but only for
    /// parameters the endpoint URI did not set explicitly — an inline URI value always wins.
    /// </summary>
    internal void ApplyTo(SignalREndpointOptions options, EndpointUri uri)
    {
        var supplied = uri.RawParameters;

        if (!supplied.ContainsKey(nameof(options.AccessToken))) options.AccessToken = AccessToken;
        if (!supplied.ContainsKey(nameof(options.Transport))) options.Transport = Transport;
        if (!supplied.ContainsKey(nameof(options.Ssl))) options.Ssl = Ssl;
        if (!supplied.ContainsKey(nameof(options.SslCertPath))) options.SslCertPath = SslCertPath;
        if (!supplied.ContainsKey(nameof(options.SslCertPassword)))
            options.SslCertPassword = SslCertPassword;
    }
}

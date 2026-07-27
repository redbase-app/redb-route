using redb.Route.Abstractions;

namespace redb.Route.Ldap;

/// <summary>
/// Named connection factory for LDAP. Register it in the route registry and reference it
/// by name from the endpoint URI (<c>connectionFactory=my-ldap</c>) so that service-account
/// credentials never have to appear in the URI itself — and therefore can never reach logs,
/// telemetry, or the Tsak dashboard.
/// <para>
/// Example:
/// <code>
/// context.AddToRegistry("honest-ldap", new LdapConnectionFactory
/// {
///     Server = "ldap.corp.local",
///     Port = 636,
///     Ssl = true,
///     BindDn = "cn=svc-reader,dc=corp,dc=local",
///     BindPassword = Environment.GetEnvironmentVariable("LDAP_BIND_PASSWORD")
/// });
///
/// // route carries no credentials at all:
/// r.From("ldap://SEARCH:dc=corp,dc=local?connectionFactory=honest-ldap&amp;filter=(objectClass=user)")
/// </code>
/// </para>
/// </summary>
public sealed class LdapConnectionFactory
{
    // ── Connection ──

    /// <summary>LDAP server hostname or IP.</summary>
    public string Server { get; set; } = "localhost";

    /// <summary>LDAP port (389 for plain, 636 for LDAPS).</summary>
    public int Port { get; set; } = 389;

    /// <summary>Use LDAPS (SSL/TLS from the start, typically port 636).</summary>
    public bool Ssl { get; set; }

    /// <summary>Use STARTTLS upgrade on the plain port (389).</summary>
    public bool StartTls { get; set; }

    /// <summary>Connection timeout in milliseconds.</summary>
    public int ConnectTimeout { get; set; } = 5000;

    /// <summary>Operation timeout in milliseconds.</summary>
    public int OperationTimeout { get; set; } = 30000;

    // ── Auth (service account) ──

    /// <summary>Bind DN for service account authentication.</summary>
    public string? BindDn { get; set; }

    /// <summary>Password for service account authentication.</summary>
    public string? BindPassword { get; set; }

    // ── Protocol / pool ──

    /// <summary>LDAP protocol version (default 3).</summary>
    public int ProtocolVersion { get; set; } = 3;

    /// <summary>Follow LDAP referrals automatically.</summary>
    public bool FollowReferrals { get; set; } = true;

    /// <summary>Maximum number of connections in the pool.</summary>
    public int MaxConnections { get; set; } = 10;

    // ── TLS ──

    /// <summary>Skip server certificate validation (development only!).</summary>
    public bool SkipCertificateValidation { get; set; }

    /// <summary>Path to client certificate for mutual TLS.</summary>
    public string? ClientCertPath { get; set; }

    /// <summary>Password for client certificate.</summary>
    public string? ClientCertPassword { get; set; }

    /// <summary>
    /// Copies this factory's connection and credential settings onto the endpoint options,
    /// but only for parameters the endpoint URI did not set explicitly — an inline URI value
    /// always wins, so existing routes keep their behaviour.
    /// </summary>
    /// <param name="options">Endpoint options already bound from the URI.</param>
    /// <param name="uri">The parsed endpoint URI, used to detect explicitly-supplied parameters.</param>
    internal void ApplyTo(LdapEndpointOptions options, EndpointUri uri)
    {
        var supplied = uri.RawParameters;

        if (!supplied.ContainsKey(nameof(options.Server))) options.Server = Server;
        if (!supplied.ContainsKey(nameof(options.Port))) options.Port = Port;
        if (!supplied.ContainsKey(nameof(options.Ssl))) options.Ssl = Ssl;
        if (!supplied.ContainsKey(nameof(options.StartTls))) options.StartTls = StartTls;
        if (!supplied.ContainsKey(nameof(options.ConnectTimeout))) options.ConnectTimeout = ConnectTimeout;
        if (!supplied.ContainsKey(nameof(options.OperationTimeout))) options.OperationTimeout = OperationTimeout;

        if (!supplied.ContainsKey(nameof(options.BindDn))) options.BindDn = BindDn;
        if (!supplied.ContainsKey(nameof(options.BindPassword))) options.BindPassword = BindPassword;

        if (!supplied.ContainsKey(nameof(options.ProtocolVersion))) options.ProtocolVersion = ProtocolVersion;
        if (!supplied.ContainsKey(nameof(options.FollowReferrals))) options.FollowReferrals = FollowReferrals;
        if (!supplied.ContainsKey(nameof(options.MaxConnections))) options.MaxConnections = MaxConnections;

        if (!supplied.ContainsKey(nameof(options.SkipCertificateValidation)))
            options.SkipCertificateValidation = SkipCertificateValidation;
        if (!supplied.ContainsKey(nameof(options.ClientCertPath))) options.ClientCertPath = ClientCertPath;
        if (!supplied.ContainsKey(nameof(options.ClientCertPassword)))
            options.ClientCertPassword = ClientCertPassword;
    }
}

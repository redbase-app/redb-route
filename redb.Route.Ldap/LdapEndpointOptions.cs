using redb.Route.Core;

namespace redb.Route.Ldap;

/// <summary>
/// Typed options for LDAP endpoint. Bound from URI query parameters.
/// </summary>
public sealed class LdapEndpointOptions : EndpointOptions
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

    /// <summary>Named connection factory from the registry.</summary>
    public string? ConnectionFactory { get; set; }

    /// <summary>Connection timeout in milliseconds.</summary>
    public int ConnectTimeout { get; set; } = 5000;

    /// <summary>Operation timeout in milliseconds.</summary>
    public int OperationTimeout { get; set; } = 30000;

    // ── Auth (service account) ──

    /// <summary>Bind DN for service account authentication.</summary>
    public string? BindDn { get; set; }

    /// <summary>Password for service account authentication.</summary>
    [Sensitive]
    public string? BindPassword { get; set; }

    // ── Search ──

    /// <summary>
    /// LDAP filter expression, e.g. <c>(objectClass=user)</c>.
    /// Supports <c>${...}</c> expressions via <see cref="DynamicValue{T}"/> for per-request resolution.
    /// </summary>
    public DynamicValue<string>? Filter { get; set; }

    /// <summary>Search scope: base, onelevel, or subtree (default).</summary>
    public string Scope { get; set; } = "subtree";

    /// <summary>Comma-separated list of attributes to return, e.g. <c>cn,mail,sAMAccountName</c>.</summary>
    public string? Attributes { get; set; }

    /// <summary>Page size for paged search results (default 500).</summary>
    public int PageSize { get; set; } = 500;

    /// <summary>Server-side maximum number of entries to return (0 = unlimited).</summary>
    public int SizeLimit { get; set; }

    /// <summary>Server-side time limit in seconds (0 = unlimited).</summary>
    public int TimeLimit { get; set; }

    // ── Consumer (Watch) ──

    /// <summary>Poll interval in milliseconds for the Watch consumer (default 60000).</summary>
    public int PollInterval { get; set; } = 60000;

    /// <summary>Change tracking mode: modifyTimestamp, usn, or persistent.</summary>
    public string ChangeTrackingMode { get; set; } = "modifyTimestamp";

    /// <summary>Load all matching entries on consumer startup.</summary>
    public bool InitialLoad { get; set; }

    /// <summary>Detect deleted entries by comparing known DNs with current search results (opt-in).</summary>
    public bool DetectDeletions { get; set; }

    /// <summary>Number of poll cycles between full synchronizations for deletion detection (default 10).</summary>
    public int FullSyncInterval { get; set; } = 10;

    // ── Protocol ──

    /// <summary>LDAP protocol version (default 3).</summary>
    public int ProtocolVersion { get; set; } = 3;

    /// <summary>Follow LDAP referrals automatically.</summary>
    public bool FollowReferrals { get; set; } = true;

    // ── Pool ──

    /// <summary>Maximum number of connections in the pool.</summary>
    public int MaxConnections { get; set; } = 10;

    // ── SSL options ──

    /// <summary>Skip server certificate validation (development only!).</summary>
    public bool SkipCertificateValidation { get; set; }

    /// <summary>Path to client certificate for mutual TLS.</summary>
    public string? ClientCertPath { get; set; }

    /// <summary>Password for client certificate.</summary>
    [Sensitive]
    public string? ClientCertPassword { get; set; }

    /// <inheritdoc />
    public override void Validate()
    {
        if (string.IsNullOrWhiteSpace(Server))
            throw new ArgumentException("LDAP server is required.", nameof(Server));
        if (Port is <= 0 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(Port), Port, "Port must be between 1 and 65535.");
        if (Ssl && StartTls)
            throw new InvalidOperationException("Ssl and StartTls are mutually exclusive. Use Ssl for LDAPS (port 636) or StartTls for upgrading a plain connection (port 389).");
        if (PageSize < 0)
            throw new ArgumentOutOfRangeException(nameof(PageSize), PageSize, "PageSize must be >= 0 (0 disables paged results).");
        if (MaxConnections <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaxConnections), MaxConnections, "MaxConnections must be > 0.");
        if (ConnectTimeout <= 0)
            throw new ArgumentOutOfRangeException(nameof(ConnectTimeout), ConnectTimeout, "ConnectTimeout must be > 0.");
        if (OperationTimeout <= 0)
            throw new ArgumentOutOfRangeException(nameof(OperationTimeout), OperationTimeout, "OperationTimeout must be > 0.");
        if (PollInterval <= 0)
            throw new ArgumentOutOfRangeException(nameof(PollInterval), PollInterval, "PollInterval must be > 0.");
        if (FullSyncInterval <= 0)
            throw new ArgumentOutOfRangeException(nameof(FullSyncInterval), FullSyncInterval, "FullSyncInterval must be > 0.");
    }

    /// <summary>
    /// Parses the <see cref="Scope"/> string into a <see cref="LdapSearchScope"/> enum.
    /// </summary>
    internal LdapSearchScope ParseScope() => Scope?.ToLowerInvariant() switch
    {
        "base" => LdapSearchScope.Base,
        "onelevel" or "one" => LdapSearchScope.OneLevel,
        _ => LdapSearchScope.Subtree
    };

    /// <summary>
    /// Parses the <see cref="Attributes"/> CSV string into an array.
    /// Returns null if no specific attributes are requested (= return all).
    /// </summary>
    internal string[]? ParseAttributes() =>
        string.IsNullOrWhiteSpace(Attributes) ? null : Attributes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>
    /// Parses the <see cref="ChangeTrackingMode"/> string into enum.
    /// </summary>
    internal LdapChangeTrackingMode ParseChangeTrackingMode() => ChangeTrackingMode?.ToLowerInvariant() switch
    {
        "usn" => LdapChangeTrackingMode.Usn,
        "persistent" => LdapChangeTrackingMode.Persistent,
        _ => LdapChangeTrackingMode.ModifyTimestamp
    };

    /// <summary>
    /// Gets the effective LDAP port: if SSL is enabled and default port is still 389, switch to 636.
    /// </summary>
    internal int EffectivePort => Ssl && Port == 389 ? 636 : Port;
}

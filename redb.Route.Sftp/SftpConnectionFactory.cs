using redb.Route.Abstractions;
using redb.Route.GenericFile;

namespace redb.Route.Sftp;

/// <summary>
/// Named connection factory for SFTP. Register it in the route registry and reference it by name
/// from the endpoint URI (<c>connectionFactory=my-sftp</c>) so the password, private-key
/// passphrase and proxy password never have to appear in the URI, and therefore can never reach
/// logs, telemetry, or the dashboard.
/// <para>
/// Example:
/// <code>
/// context.AddToRegistry("partner-sftp", new SftpConnectionFactory
/// {
///     Host = "sftp.partner.com", Port = 22, Username = "svc-drop",
///     PrivateKeyPath = "/etc/redb/keys/partner.pem",
///     PrivateKeyPassphrase = Environment.GetEnvironmentVariable("SFTP_PASSPHRASE")!
/// });
///
/// r.From("sftp://inbox?connectionFactory=partner-sftp")   // no secrets in the route
/// </code>
/// </para>
/// </summary>
public sealed class SftpConnectionFactory : RemoteFileConnectionFactory
{
    /// <summary>Path to the private key file.</summary>
    public string PrivateKeyPath { get; set; } = "";

    /// <summary>Passphrase protecting the private key.</summary>
    public string PrivateKeyPassphrase { get; set; } = "";

    /// <summary>Preferred authentication methods, comma-separated.</summary>
    public string PreferredAuthentications { get; set; } = "";

    /// <summary>Expected server host-key fingerprint.</summary>
    public string ServerFingerprint { get; set; } = "";

    /// <summary>Enforce known-hosts checking.</summary>
    public bool StrictHostKeyChecking { get; set; }

    /// <summary>Path to the known_hosts file.</summary>
    public string KnownHostsFile { get; set; } = "";

    /// <summary>Proxy host, when connecting through a proxy.</summary>
    public string ProxyHost { get; set; } = "";

    /// <summary>Proxy port.</summary>
    public int ProxyPort { get; set; } = 1080;

    /// <summary>Proxy username.</summary>
    public string ProxyUsername { get; set; } = "";

    /// <summary>Proxy password.</summary>
    public string ProxyPassword { get; set; } = "";

    /// <inheritdoc />
    internal override void ApplyTo(RemoteFileEndpointOptions options, EndpointUri uri)
    {
        base.ApplyTo(options, uri);

        if (options is not SftpEndpointOptions sftp) return;
        var supplied = uri.RawParameters;

        if (!supplied.ContainsKey(nameof(sftp.PrivateKeyPath))) sftp.PrivateKeyPath = PrivateKeyPath;
        if (!supplied.ContainsKey(nameof(sftp.PrivateKeyPassphrase)))
            sftp.PrivateKeyPassphrase = PrivateKeyPassphrase;
        if (!supplied.ContainsKey(nameof(sftp.PreferredAuthentications)))
            sftp.PreferredAuthentications = PreferredAuthentications;
        if (!supplied.ContainsKey(nameof(sftp.ServerFingerprint)))
            sftp.ServerFingerprint = ServerFingerprint;
        if (!supplied.ContainsKey(nameof(sftp.StrictHostKeyChecking)))
            sftp.StrictHostKeyChecking = StrictHostKeyChecking;
        if (!supplied.ContainsKey(nameof(sftp.KnownHostsFile))) sftp.KnownHostsFile = KnownHostsFile;

        if (!supplied.ContainsKey(nameof(sftp.ProxyHost))) sftp.ProxyHost = ProxyHost;
        if (!supplied.ContainsKey(nameof(sftp.ProxyPort))) sftp.ProxyPort = ProxyPort;
        if (!supplied.ContainsKey(nameof(sftp.ProxyUsername))) sftp.ProxyUsername = ProxyUsername;
        if (!supplied.ContainsKey(nameof(sftp.ProxyPassword))) sftp.ProxyPassword = ProxyPassword;
    }
}

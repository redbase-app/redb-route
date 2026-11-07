using redb.Route.Abstractions;
using redb.Route.GenericFile;

namespace redb.Route.Ftp;

/// <summary>
/// Named connection factory for FTP/FTPS. Register it in the route registry and reference it by
/// name from the endpoint URI (<c>connectionFactory=my-ftp</c>) so the server password never has
/// to appear in the URI, and therefore can never reach logs, telemetry, or the dashboard.
/// <para>
/// Example:
/// <code>
/// context.AddToRegistry("partner-ftp", new FtpConnectionFactory
/// {
///     Host = "ftp.partner.com", Port = 21, UseFtps = true,
///     Username = "svc-drop",
///     Password = Environment.GetEnvironmentVariable("FTP_PASSWORD")!
/// });
///
/// r.From("ftp://inbox?connectionFactory=partner-ftp")   // no credentials in the route
/// </code>
/// </para>
/// </summary>
public sealed class FtpConnectionFactory : RemoteFileConnectionFactory
{
    /// <summary>Use passive mode (default true).</summary>
    public bool PassiveMode { get; set; } = true;

    /// <summary>Use FTPS (TLS).</summary>
    public bool UseFtps { get; set; }

    /// <summary>Validate the server TLS certificate (default true).</summary>
    public bool ValidateCertificate { get; set; } = true;

    /// <inheritdoc />
    internal override void ApplyTo(RemoteFileEndpointOptions options, EndpointUri uri)
    {
        base.ApplyTo(options, uri);

        if (options is not FtpEndpointOptions ftp) return;
        var supplied = uri.RawParameters;

        if (!supplied.ContainsKey(nameof(ftp.PassiveMode))) ftp.PassiveMode = PassiveMode;
        if (!supplied.ContainsKey(nameof(ftp.UseFtps))) ftp.UseFtps = UseFtps;
        if (!supplied.ContainsKey(nameof(ftp.ValidateCertificate)))
            ftp.ValidateCertificate = ValidateCertificate;
    }
}

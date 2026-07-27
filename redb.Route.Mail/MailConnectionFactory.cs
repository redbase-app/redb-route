using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;

namespace redb.Route.Mail;

/// <summary>
/// Named connection factory for mail transports (SMTP, IMAP, POP3). Register it in the route
/// registry and reference it by name from the endpoint URI (<c>connectionFactory=my-mailbox</c>)
/// so mailbox credentials never have to appear in the URI, and therefore can never reach logs,
/// telemetry, or the Tsak dashboard. One factory serves all three protocols.
/// <para>
/// Example:
/// <code>
/// context.AddToRegistry("corp-mailbox", new MailConnectionFactory
/// {
///     Host = "mail.corp.local",
///     Port = 993,
///     Security = MailSecurityMode.SslOnConnect,
///     Username = "svc-reports",
///     Password = Environment.GetEnvironmentVariable("MAIL_PASSWORD")!
/// });
///
/// // route carries no credentials at all:
/// r.From("imap://?connectionFactory=corp-mailbox&amp;folder=INBOX")
/// </code>
/// </para>
/// </summary>
public sealed class MailConnectionFactory
{
    // ── Connection ──

    /// <summary>Mail server hostname or IP.</summary>
    public string Host { get; set; } = "localhost";

    /// <summary>Server port (0 = protocol default for the chosen security mode).</summary>
    public int Port { get; set; }

    /// <summary>Transport security mode (Auto, None, SslOnConnect, StartTls…).</summary>
    public MailSecurityMode Security { get; set; } = MailSecurityMode.Auto;

    /// <summary>Connection timeout in milliseconds.</summary>
    public int ConnectionTimeout { get; set; } = 30_000;

    /// <summary>Operation timeout in milliseconds.</summary>
    public int Timeout { get; set; } = 60_000;

    // ── Auth ──

    /// <summary>Mailbox username.</summary>
    public string Username { get; set; } = "";

    /// <summary>Mailbox password.</summary>
    public string Password { get; set; } = "";

    /// <summary>OAuth2 access token (used instead of the password when set).</summary>
    public string AccessToken { get; set; } = "";

    /// <summary>Authentication mechanism.</summary>
    public MailAuthMechanism AuthMechanism { get; set; } = MailAuthMechanism.Auto;

    // ── TLS ──

    /// <summary>Skip server certificate validation (development only!).</summary>
    public bool SkipCertificateValidation { get; set; }

    /// <summary>Path to the client certificate for mutual TLS.</summary>
    public string ClientCertPath { get; set; } = "";

    /// <summary>Password for the client certificate.</summary>
    public string ClientCertPassword { get; set; } = "";

    /// <summary>
    /// Copies this factory's connection and credential settings onto the endpoint options, but only
    /// for parameters the endpoint URI did not set explicitly — an inline URI value always wins,
    /// so existing routes keep their behaviour. <see cref="Host"/> is additionally skipped when the
    /// URI path already carried the host (<c>imap://mail.corp.local</c>), so a factory can never
    /// silently redirect a route to a different server.
    /// </summary>
    internal void ApplyTo(MailEndpointOptions options, EndpointUri uri)
    {
        var supplied = uri.RawParameters;
        var hostFromPath = !string.IsNullOrEmpty(uri.Path) && uri.Path != "/";

        if (!supplied.ContainsKey(nameof(options.Host)) && !hostFromPath) options.Host = Host;
        if (!supplied.ContainsKey(nameof(options.Port))) options.Port = Port;
        if (!supplied.ContainsKey(nameof(options.Security))) options.Security = Security;
        if (!supplied.ContainsKey(nameof(options.ConnectionTimeout)))
            options.ConnectionTimeout = ConnectionTimeout;
        if (!supplied.ContainsKey(nameof(options.Timeout))) options.Timeout = Timeout;

        if (!supplied.ContainsKey(nameof(options.Username))) options.Username = Username;
        if (!supplied.ContainsKey(nameof(options.Password))) options.Password = Password;
        if (!supplied.ContainsKey(nameof(options.AccessToken))) options.AccessToken = AccessToken;
        if (!supplied.ContainsKey(nameof(options.AuthMechanism))) options.AuthMechanism = AuthMechanism;

        if (!supplied.ContainsKey(nameof(options.SkipCertificateValidation)))
            options.SkipCertificateValidation = SkipCertificateValidation;
        if (!supplied.ContainsKey(nameof(options.ClientCertPath))) options.ClientCertPath = ClientCertPath;
        if (!supplied.ContainsKey(nameof(options.ClientCertPassword)))
            options.ClientCertPassword = ClientCertPassword;
    }

    /// <summary>
    /// Shared resolution step for the SMTP / IMAP / POP3 components: looks the named factory up in
    /// the registry and applies it. Called after <c>BindFromUri</c> and before <c>Validate()</c>.
    /// </summary>
    internal static void TryApply(
        IRouteContext? context, MailEndpointOptions options, EndpointUri uri,
        ILogger? logger, string protocol)
    {
        if (string.IsNullOrEmpty(options.ConnectionFactory) || context is null)
            return;

        var factory = context.GetFromRegistry<MailConnectionFactory>(options.ConnectionFactory);
        if (factory is not null)
            factory.ApplyTo(options, uri);
        else
            logger?.LogWarning(
                "{Protocol}: ConnectionFactory '{Name}' not found in registry, falling back to URI parameters",
                protocol, options.ConnectionFactory);
    }
}

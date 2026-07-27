using redb.Route.Core;

namespace redb.Route.Mail;

/// <summary>
/// Options for mail endpoints (SMTP, IMAP, POP3). Bound from URI query parameters.
/// Provides Camel-level configurability for enterprise email integration.
/// </summary>
/// <remarks>
/// <para>SMTP URI: smtp://mail.example.com:587?username=bot@ex.com&amp;password=xxx&amp;security=StartTls</para>
/// <para>IMAP URI: imap://mail.example.com:993?username=inbox@ex.com&amp;password=xxx&amp;folder=INBOX&amp;unseen=true</para>
/// <para>POP3 URI: pop3://mail.example.com:995?username=inbox@ex.com&amp;password=xxx&amp;delete=true</para>
/// </remarks>
public class MailEndpointOptions : EndpointOptions
{
    // ── Connection ────────────────────────────────────────────────────

    /// <summary>Mail server hostname. Resolved from URI host or this parameter.</summary>
    public string Host { get; set; } = "localhost";

    /// <summary>Mail server port. 0 = auto-detect from protocol and security mode.</summary>
    public int Port { get; set; }

    /// <summary>Connection security mode (None, Ssl, StartTls, Auto).</summary>
    public MailSecurityMode Security { get; set; } = MailSecurityMode.Auto;

    /// <summary>Connection timeout in milliseconds.</summary>
    public int ConnectionTimeout { get; set; } = 30_000;

    /// <summary>Socket read/write timeout in milliseconds.</summary>
    public int Timeout { get; set; } = 60_000;

    // ── Authentication ────────────────────────────────────────────────

    /// <summary>
    /// Named <see cref="MailConnectionFactory"/> from the route registry. Lets mailbox credentials
    /// live in the registry instead of the endpoint URI, so they never reach logs or dashboards.
    /// </summary>
    public string? ConnectionFactory { get; set; }

    /// <summary>Username for authentication.</summary>
    public string Username { get; set; } = "";

    /// <summary>Password for authentication.</summary>
    [Sensitive]
    public string Password { get; set; } = "";

    /// <summary>OAuth2 access token (for XOAUTH2/OAUTHBEARER).</summary>
    [Sensitive]
    public string AccessToken { get; set; } = "";

    /// <summary>Authentication mechanism to use.</summary>
    public MailAuthMechanism AuthMechanism { get; set; } = MailAuthMechanism.Auto;

    // ── TLS/SSL ───────────────────────────────────────────────────────

    /// <summary>Skip server certificate validation (development only!).</summary>
    public bool SkipCertificateValidation { get; set; }

    /// <summary>Client certificate path for mutual TLS.</summary>
    public string ClientCertPath { get; set; } = "";

    /// <summary>Client certificate password.</summary>
    [Sensitive]
    public string ClientCertPassword { get; set; } = "";

    // ── SMTP Producer options ─────────────────────────────────────────

    /// <summary>Default From address for sent emails. Can be overridden by MailHeaders.From header.</summary>
    public string From { get; set; } = "";

    /// <summary>Default To addresses (comma-separated). Can be overridden by MailHeaders.To header.</summary>
    public string To { get; set; } = "";

    /// <summary>Default CC addresses (comma-separated).</summary>
    public string Cc { get; set; } = "";

    /// <summary>Default BCC addresses (comma-separated).</summary>
    public string Bcc { get; set; } = "";

    /// <summary>Default Reply-To address.</summary>
    public string ReplyTo { get; set; } = "";

    /// <summary>Default subject line. Supports Simple expressions: ${header.orderId}.</summary>
    public string Subject { get; set; } = "";

    /// <summary>Content type of the email body: text/plain or text/html.</summary>
    public string ContentType { get; set; } = "text/plain";

    /// <summary>Alternative body (e.g. text/plain alternative when body is HTML).</summary>
    public bool AlternativeBody { get; set; }

    /// <summary>Comma-separated file paths for attachments.</summary>
    public string Attachments { get; set; } = "";

    // ── IMAP/POP3 Consumer options ────────────────────────────────────

    /// <summary>IMAP folder to monitor (default: INBOX).</summary>
    public string Folder { get; set; } = "INBOX";

    /// <summary>Additional IMAP folders to monitor (comma-separated).</summary>
    public string AdditionalFolders { get; set; } = "";

    /// <summary>Polling interval in milliseconds.</summary>
    public int Delay { get; set; } = 60_000;

    /// <summary>Initial delay before first poll in milliseconds.</summary>
    public int InitialDelay { get; set; }

    /// <summary>Message filter for fetch (Unseen, All, Recent, Flagged, etc.).</summary>
    public MailFetchFilter FetchFilter { get; set; } = MailFetchFilter.Unseen;

    /// <summary>Maximum number of messages to fetch per poll cycle.</summary>
    public int MaxMessages { get; set; } = 50;

    /// <summary>What to do with messages after successful processing.</summary>
    public PostProcessAction PostProcess { get; set; } = PostProcessAction.MarkRead;

    /// <summary>Target folder for Move/MarkReadAndMove post-processing (IMAP only).</summary>
    public string MoveTo { get; set; } = "";

    /// <summary>Sort order for fetched messages.</summary>
    public MailSortBy SortBy { get; set; } = MailSortBy.None;

    /// <summary>Whether to use IMAP IDLE for push notifications instead of polling.</summary>
    public bool Idle { get; set; }

    /// <summary>IMAP IDLE timeout in milliseconds (max time to wait before re-issuing IDLE).</summary>
    public int IdleTimeout { get; set; } = 29 * 60 * 1000; // 29 minutes (RFC 2177 recommends &lt; 30 min)

    /// <summary>Whether to download message body (false = headers only for envelope scanning).</summary>
    public bool FetchBody { get; set; } = true;

    /// <summary>Whether to download attachments.</summary>
    public bool FetchAttachments { get; set; } = true;

    /// <summary>Maximum attachment size in bytes to download (0 = unlimited).</summary>
    public long MaxAttachmentSize { get; set; }

    /// <summary>Whether to peek at messages (IMAP: don't set \Seen flag on fetch).</summary>
    public bool Peek { get; set; }

    /// <summary>Whether to use idempotent processing (skip already-seen message IDs).</summary>
    public bool Idempotent { get; set; }

    /// <summary>Custom IMAP search query (overrides FetchFilter). Uses IMAP SEARCH syntax.</summary>
    public string SearchQuery { get; set; } = "";

    /// <summary>Minimum message age in milliseconds (skip messages newer than this).</summary>
    public long MinAge { get; set; }

    /// <summary>Maximum message age in milliseconds (skip messages older than this).</summary>
    public long MaxAge { get; set; }

    /// <summary>Subject filter — only process messages matching this pattern (regex).</summary>
    public string SubjectFilter { get; set; } = "";

    /// <summary>From filter — only process messages from these addresses (comma-separated).</summary>
    public string FromFilter { get; set; } = "";

    /// <summary>Whether to include MIME headers as exchange headers.</summary>
    public bool MapMimeHeaders { get; set; }

    /// <summary>Whether to keep the raw MimeMessage in exchange properties.</summary>
    public bool KeepRawMessage { get; set; }

    // ── Connection pooling ────────────────────────────────────────────

    /// <summary>Whether to disconnect after each operation (default: maintain persistent connection).</summary>
    public bool Disconnect { get; set; }

    /// <inheritdoc />
    public override void Validate()
    {
        if (Port < 0 || Port > 65535)
            throw new ArgumentOutOfRangeException(nameof(Port), "Port must be 0-65535.");
        if (Delay < 0)
            throw new ArgumentOutOfRangeException(nameof(Delay), "Delay must be non-negative.");
        if (MaxMessages < 0)
            throw new ArgumentOutOfRangeException(nameof(MaxMessages), "MaxMessages must be non-negative.");
        if (PostProcess is PostProcessAction.Move or PostProcessAction.MarkReadAndMove
            && string.IsNullOrWhiteSpace(MoveTo))
            throw new InvalidOperationException("MoveTo is required when PostProcess is Move or MarkReadAndMove.");
        if (ConnectionTimeout < 0)
            throw new ArgumentOutOfRangeException(nameof(ConnectionTimeout));
        if (Timeout < 0)
            throw new ArgumentOutOfRangeException(nameof(Timeout));
    }

    /// <summary>
    /// Resolves the effective port based on protocol scheme and security mode.
    /// </summary>
    internal int ResolvePort(string scheme)
    {
        if (Port > 0) return Port;

        return scheme switch
        {
            "smtp" => Security switch
            {
                MailSecurityMode.Ssl => 465,
                MailSecurityMode.None => 25,
                _ => 587
            },
            "imap" => Security is MailSecurityMode.None ? 143 : 993,
            "pop3" => Security is MailSecurityMode.None ? 110 : 995,
            _ => 25
        };
    }

    /// <summary>
    /// Resolves MailKit SecureSocketOptions from the Security setting.
    /// </summary>
    internal MailKit.Security.SecureSocketOptions ResolveSecurityOptions()
    {
        return Security switch
        {
            MailSecurityMode.None => MailKit.Security.SecureSocketOptions.None,
            MailSecurityMode.Ssl => MailKit.Security.SecureSocketOptions.SslOnConnect,
            MailSecurityMode.StartTls => MailKit.Security.SecureSocketOptions.StartTls,
            MailSecurityMode.Auto => MailKit.Security.SecureSocketOptions.Auto,
            _ => MailKit.Security.SecureSocketOptions.Auto
        };
    }
}

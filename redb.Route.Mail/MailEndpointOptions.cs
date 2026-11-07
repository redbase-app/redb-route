using redb.Route.Abstractions;
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
    [RegistryReference(typeof(MailConnectionFactory))]
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
    public bool TrustAllCertificates { get; set; }

    /// <summary>Client certificate path for mutual TLS.</summary>
    public string ClientCertPath { get; set; } = "";

    /// <summary>Client certificate password.</summary>
    [Sensitive]
    public string ClientCertPassword { get; set; } = "";

    // ── SMTP Producer options ─────────────────────────────────────────

    /// <summary>Default From address for sent emails. Can be overridden by MailHeaders.From header.</summary>
    [EndpointRole(EndpointRole.Producer)]
    public string From { get; set; } = "";

    /// <summary>Default To addresses (comma-separated). Can be overridden by MailHeaders.To header.</summary>
    [EndpointRole(EndpointRole.Producer)]
    public string To { get; set; } = "";

    /// <summary>Default CC addresses (comma-separated).</summary>
    [EndpointRole(EndpointRole.Producer)]
    public string Cc { get; set; } = "";

    /// <summary>Default BCC addresses (comma-separated).</summary>
    [EndpointRole(EndpointRole.Producer)]
    public string Bcc { get; set; } = "";

    /// <summary>Default Reply-To address.</summary>
    [EndpointRole(EndpointRole.Producer)]
    public string ReplyTo { get; set; } = "";

    /// <summary>Default subject line. Supports Simple expressions: ${header.orderId}.</summary>
    [EndpointRole(EndpointRole.Producer)]
    public string Subject { get; set; } = "";

    /// <summary>Content type of the email body: text/plain or text/html.</summary>
    [EndpointRole(EndpointRole.Producer)]
    public string ContentType { get; set; } = "text/plain";

    /// <summary>Alternative body (e.g. text/plain alternative when body is HTML).</summary>
    [EndpointRole(EndpointRole.Producer)]
    public bool AlternativeBody { get; set; }

    /// <summary>Comma-separated file paths for attachments.</summary>
    [EndpointRole(EndpointRole.Producer)]
    public string Attachments { get; set; } = "";

    // ── IMAP/POP3 Consumer options ────────────────────────────────────

    /// <summary>IMAP folder to monitor (default: INBOX).</summary>
    [EndpointRole(EndpointRole.Consumer)]
    public string Folder { get; set; } = "INBOX";

    /// <summary>Additional IMAP folders to monitor (comma-separated).</summary>
    [EndpointRole(EndpointRole.Consumer)]
    public string AdditionalFolders { get; set; } = "";

    /// <summary>Polling interval in milliseconds.</summary>
    [EndpointRole(EndpointRole.Consumer)]
    public int Delay { get; set; } = 60_000;

    /// <summary>Initial delay before first poll in milliseconds.</summary>
    [EndpointRole(EndpointRole.Consumer)]
    public int InitialDelay { get; set; }

    /// <summary>Message filter for fetch (Unseen, All, Recent, Flagged, etc.).</summary>
    [EndpointRole(EndpointRole.Consumer)]
    public MailFetchFilter FetchFilter { get; set; } = MailFetchFilter.Unseen;

    /// <summary>Maximum number of messages to fetch per poll cycle.</summary>
    [EndpointRole(EndpointRole.Consumer)]
    public int MaxMessages { get; set; } = 50;

    /// <summary>
    /// What to do with a message whose exchange succeeded (Apache Camel's commit). An exchange that failed, with an
    /// unhandled exception or marked rollback-only (<c>.RollbackAll()</c>), leaves the message as it is (Camel's
    /// rollback); a handled exception is a success.
    /// </summary>
    [EndpointRole(EndpointRole.Consumer)]
    public PostProcessAction PostProcess
    {
        get => _postProcess;
        set { _postProcess = value; _postProcessSet = true; }
    }

    private PostProcessAction _postProcess = PostProcessAction.MarkRead;
    private bool _postProcessSet;

    /// <summary>
    /// Apache Camel's <c>delete</c>, beside <see cref="PostProcess"/>: <c>true</c> deletes a message whose exchange
    /// succeeded, the same as <c>postProcess=Delete</c>, moving it to <see cref="MoveTo"/> first when that is set;
    /// <c>false</c> leaves <see cref="PostProcess"/> to decide. A value that contradicts an explicit
    /// <see cref="PostProcess"/> fails validation. The header <see cref="MailHeaders.Delete"/> overrides it per message.
    /// </summary>
    [EndpointRole(EndpointRole.Consumer)]
    public bool Delete
    {
        get => _delete;
        set { _delete = value; _deleteSet = true; }
    }

    private bool _delete;
    private bool _deleteSet;

    /// <summary>
    /// Target folder for Move/MarkReadAndMove post-processing, and for <c>delete=true</c> (IMAP only); created when
    /// the mailbox does not have it.
    /// </summary>
    [EndpointRole(EndpointRole.Consumer)]
    public string MoveTo { get; set; } = "";

    /// <summary>
    /// Apache Camel's <c>copyTo</c>: an IMAP folder a message whose exchange succeeded is copied to before anything
    /// else is done to it. The header <see cref="MailHeaders.CopyTo"/> overrides it per message. Created when
    /// missing; POP3 ignores it.
    /// </summary>
    [EndpointRole(EndpointRole.Consumer)]
    public string CopyTo { get; set; } = "";

    /// <summary>Sort order for fetched messages.</summary>
    [EndpointRole(EndpointRole.Consumer)]
    public MailSortBy SortBy { get; set; } = MailSortBy.None;

    /// <summary>Whether to use IMAP IDLE for push notifications instead of polling.</summary>
    [EndpointRole(EndpointRole.Consumer)]
    public bool Idle { get; set; }

    /// <summary>IMAP IDLE timeout in milliseconds (max time to wait before re-issuing IDLE).</summary>
    [EndpointRole(EndpointRole.Consumer)]
    public int IdleTimeout { get; set; } = 29 * 60 * 1000; // 29 minutes (RFC 2177 recommends &lt; 30 min)

    /// <summary>Whether to download message body (false = headers only for envelope scanning).</summary>
    [EndpointRole(EndpointRole.Consumer)]
    public bool FetchBody { get; set; } = true;

    /// <summary>Whether to carry attachment payloads on the exchange (names/count always stay).</summary>
    [EndpointRole(EndpointRole.Consumer)]
    public bool FetchAttachments { get; set; } = true;

    /// <summary>
    /// Maximum DECODED attachment size in bytes carried on the exchange (0 = unlimited).
    /// Caps the copy handed to the route, not the network transfer: the MIME message is already
    /// in memory when the cap is applied; an oversized attachment keeps its name in
    /// AttachmentNames but its payload is dropped.
    /// </summary>
    [EndpointRole(EndpointRole.Consumer)]
    public long MaxAttachmentSize { get; set; }

    /// <summary>
    /// IMAP, Apache Camel's <c>peek</c>: <c>true</c> (the default) fetches a message without marking it read, so a
    /// message whose exchange fails stays unseen and is fetched again; <c>false</c> marks it read when it is fetched,
    /// just before the route, so a failed message stays read and <c>fetchFilter=Unseen</c> does not fetch it again.
    /// POP3 has no read flag and ignores it.
    /// </summary>
    [EndpointRole(EndpointRole.Consumer)]
    public bool Peek { get; set; } = true;

    /// <summary>
    /// In-memory idempotency: a message this consumer routed successfully is skipped when it is fetched again, until
    /// the consumer restarts; a failed one is routed again. <see cref="IdempotentRepository"/> keeps the keys beyond a
    /// restart and takes precedence. The key is the Message-ID, else folder, UIDVALIDITY and UID (IMAP) or the UIDL
    /// (POP3).
    /// </summary>
    [EndpointRole(EndpointRole.Consumer)]
    public bool Idempotent { get; set; }

    /// <summary>
    /// Apache Camel's <c>idempotentRepository</c>: the name of an <see cref="IIdempotentRepository"/> registered with
    /// <c>context.AddIdempotentRepository(name, repository)</c> or declared as a bean. A message's key is added before
    /// its route and the message is skipped when the repository already holds it; a failed exchange removes the key,
    /// a successful one removes it or confirms it (<see cref="IdempotentRepositoryRemoveOnCommit"/>). The key is the
    /// Message-ID, else folder, UIDVALIDITY and UID (IMAP) or the UIDL (POP3). Takes precedence over
    /// <see cref="Idempotent"/>.
    /// </summary>
    [EndpointRole(EndpointRole.Consumer)]
    [RegistryReference(typeof(IIdempotentRepository))]
    public string? IdempotentRepository { get; set; }

    /// <summary>
    /// Apache Camel's <c>idempotentRepositoryRemoveOnCommit</c> (default <c>true</c>): a committed message is removed
    /// from <see cref="IdempotentRepository"/>, since it was deleted or marked read; <c>false</c> confirms its key, so
    /// it is skipped whenever it is fetched again.
    /// </summary>
    [EndpointRole(EndpointRole.Consumer)]
    public bool IdempotentRepositoryRemoveOnCommit { get; set; } = true;

    /// <summary>
    /// Not applied: the consumer selects mails by <see cref="FetchFilter"/> and the client-side filters, and does not
    /// read a raw IMAP SEARCH string. Kept so that URIs which set it still bind.
    /// </summary>
    [EndpointRole(EndpointRole.Consumer)]
    public string SearchQuery { get; set; } = "";

    /// <summary>Minimum message age in milliseconds (skip messages newer than this).</summary>
    [EndpointRole(EndpointRole.Consumer)]
    public long MinAge { get; set; }

    /// <summary>Maximum message age in milliseconds (skip messages older than this).</summary>
    [EndpointRole(EndpointRole.Consumer)]
    public long MaxAge { get; set; }

    /// <summary>Subject filter — only process messages matching this pattern (regex).</summary>
    [EndpointRole(EndpointRole.Consumer)]
    public string SubjectFilter { get; set; } = "";

    /// <summary>From filter — only process messages from these addresses (comma-separated).</summary>
    [EndpointRole(EndpointRole.Consumer)]
    public string FromFilter { get; set; } = "";

    /// <summary>Whether to include MIME headers as exchange headers.</summary>
    [EndpointRole(EndpointRole.Consumer)]
    public bool MapMimeHeaders { get; set; }

    /// <summary>Whether to keep the raw MimeMessage in exchange properties.</summary>
    [EndpointRole(EndpointRole.Consumer)]
    public bool KeepRawMessage { get; set; }

    // ── Connection pooling ────────────────────────────────────────────

    /// <summary>Whether to disconnect after each operation (default: maintain persistent connection).</summary>
    public bool Disconnect { get; set; }

    /// <inheritdoc />
    protected override string? UnknownParameterHint(string name)
        => name.Equals("skipCertificateValidation", StringComparison.OrdinalIgnoreCase)
            ? "'skipCertificateValidation' is renamed 'trustAllCertificates', the name every other connector uses."
            : null;

    /// <inheritdoc />
    public override void Validate() => Validate(side: null);

    /// <summary>
    /// Validates what the side being created reads: an smtp producer does not check the consumer options (a stray
    /// one is refused by name before, <see cref="RefuseOtherSide"/>); <c>null</c> checks everything.
    /// </summary>
    internal void Validate(EndpointRole? side)
    {
        if (Port < 0 || Port > 65535)
            throw new ArgumentOutOfRangeException(nameof(Port), "Port must be 0-65535.");
        if (ConnectionTimeout < 0)
            throw new ArgumentOutOfRangeException(nameof(ConnectionTimeout));
        if (Timeout < 0)
            throw new ArgumentOutOfRangeException(nameof(Timeout));
        if (side == EndpointRole.Producer)
            return;
        if (Delay < 0)
            throw new ArgumentOutOfRangeException(nameof(Delay), "Delay must be non-negative.");
        if (MaxMessages < 0)
            throw new ArgumentOutOfRangeException(nameof(MaxMessages), "MaxMessages must be non-negative.");
        if (_deleteSet && _postProcessSet)
        {
            if (_delete && _postProcess != PostProcessAction.Delete)
                throw new InvalidOperationException(
                    $"delete=true deletes the message, which contradicts postProcess={_postProcess}; set only one of them.");
            if (!_delete && _postProcess == PostProcessAction.Delete)
                throw new InvalidOperationException(
                    "delete=false keeps the message, which contradicts postProcess=Delete; set only one of them.");
        }
        if (PostProcess is PostProcessAction.Move or PostProcessAction.MarkReadAndMove
            && string.IsNullOrWhiteSpace(MoveTo))
            throw new InvalidOperationException("MoveTo is required when PostProcess is Move or MarkReadAndMove.");
    }

    /// <summary>
    /// Refuses the parameters <paramref name="uri"/> wrote whose option only the other side reads
    /// (<see cref="EndpointRoleAttribute"/>): imap, pop3 and smtp share these options, and such a parameter would bind
    /// and do nothing. Each mail component has one side, so it refuses them when it creates the endpoint, naming the
    /// parameters and never their values.
    /// </summary>
    internal static void RefuseOtherSide(EndpointUri uri, string scheme, EndpointRole side)
    {
        var written = EndpointOptions.WrittenForOtherRole(typeof(MailEndpointOptions), uri, side);
        if (written.Count == 0)
            return;
        var (self, reader) = side == EndpointRole.Producer
            ? ("producer", "an imap or pop3 consumer")
            : ("consumer", "an smtp producer");
        throw new InvalidOperationException(
            $"{scheme} {self} '{EndpointUri.Sanitize(uri.NormalizedKey)}' sets {string.Join(", ", written)}: " +
            $"only {reader} reads {(written.Count == 1 ? "it" : "them")}.");
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

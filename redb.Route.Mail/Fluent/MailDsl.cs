using System.Text;
using System.Web;
using redb.Route.Abstractions;

namespace redb.Route.Mail;

/// <summary>
/// Fluent API for SMTP (send email) endpoints.
/// <example><code>
/// .To(Smtp.Send("smtp.example.com").Port(587).Security("StartTls")
///     .Username("bot@example.com").Password("secret")
///     .From("bot@example.com").To("user@example.com").Subject("Alert"))
/// </code></example>
/// </summary>
public static class Smtp
{
    /// <summary>Creates an SMTP producer endpoint for the given mail server.</summary>
    public static MailBuilder Send(string server) => new("smtp", server);
}

/// <summary>
/// Fluent API for IMAP (receive email) endpoints.
/// <example><code>
/// .From(Imap.Read("imap.example.com").Port(993).Security("Ssl")
///     .Username("inbox@example.com").Password("secret")
///     .Folder("INBOX").Unseen().Delay(30000))
/// </code></example>
/// </summary>
public static class Imap
{
    /// <summary>Creates an IMAP consumer endpoint for the given mail server.</summary>
    public static MailBuilder Read(string server) => new("imap", server);
}

/// <summary>
/// Fluent API for POP3 (receive email) endpoints.
/// <example><code>
/// .From(Pop3.Read("pop3.example.com").Port(995).Security("Ssl")
///     .Username("inbox@example.com").Password("secret"))
/// </code></example>
/// </summary>
public static class Pop3
{
    /// <summary>Creates a POP3 consumer endpoint for the given mail server.</summary>
    public static MailBuilder Read(string server) => new("pop3", server);
}

/// <summary>
/// Fluent builder for Mail endpoint URIs (SMTP/IMAP/POP3).
/// </summary>
public sealed class MailBuilder
{
    private readonly string _scheme;
    private readonly string _server;

    // Connection
    private int? _port;
    private string? _security;
    private int? _connectionTimeout;
    private int? _timeout;
    private string? _username;
    private string? _password;
    private string? _connectionFactory;
    private string? _accessToken;
    private string? _authMechanism;
    private bool _skipCertificateValidation;
    private string? _clientCertPath;
    private string? _clientCertPassword;

    // SMTP (producer)
    private string? _from;
    private string? _to;
    private string? _cc;
    private string? _bcc;
    private string? _replyTo;
    private string? _subject;
    private string? _contentType;
    private bool _alternativeBody;
    private string? _attachments;

    // IMAP/POP3 (consumer)
    private string? _folder;
    private string? _additionalFolders;
    private int? _delay;
    private int? _initialDelay;
    private string? _fetchFilter;
    private int? _maxMessages;
    private string? _postProcess;
    private string? _moveTo;
    private string? _sortBy;
    private bool _idle;
    private int? _idleTimeout;
    private bool? _fetchBody;
    private bool? _fetchAttachments;
    private long? _maxAttachmentSize;
    private bool _peek;
    private bool _idempotent;
    private string? _searchQuery;
    private long? _minAge;
    private long? _maxAge;
    private string? _subjectFilter;
    private string? _fromFilter;
    private bool _mapMimeHeaders;
    private bool _keepRawMessage;
    private bool _disconnect;

    internal MailBuilder(string scheme, string server)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(server);
        _scheme = scheme;
        _server = server;
    }

    // ── Connection ────────────────────────────────────────────────────

    /// <summary>Server port. Auto-detected if 0.</summary>
    public MailBuilder Port(int port) { _port = port; return this; }

    /// <summary>Security mode: None, Ssl, StartTls, Auto.</summary>
    public MailBuilder Security(string mode) { _security = mode; return this; }

    /// <summary>Connection timeout in milliseconds. Default 30000.</summary>
    public MailBuilder ConnectionTimeout(int ms) { _connectionTimeout = ms; return this; }

    /// <summary>Operation timeout in milliseconds. Default 60000.</summary>
    public MailBuilder Timeout(int ms) { _timeout = ms; return this; }

    /// <summary>Username for authentication.</summary>
    public MailBuilder Username(string username) { _username = username; return this; }

    /// <summary>Password for authentication.</summary>
    public MailBuilder Password(string password) { _password = password; return this; }

    /// <summary>
    /// References a named <see cref="MailConnectionFactory"/> from the route registry instead of
    /// putting mailbox credentials in the URI.
    /// </summary>
    public MailBuilder ConnectionFactory(string name) { _connectionFactory = name; return this; }

    /// <summary>OAuth2/Bearer access token.</summary>
    public MailBuilder AccessToken(string token) { _accessToken = token; return this; }

    /// <summary>Auth mechanism: Auto, Plain, Login, CramMd5, XOAuth2, OAuthBearer, Ntlm.</summary>
    public MailBuilder AuthMechanism(string mechanism) { _authMechanism = mechanism; return this; }

    /// <summary>Skip SSL certificate validation.</summary>
    public MailBuilder SkipCertificateValidation() { _skipCertificateValidation = true; return this; }

    /// <summary>Client certificate for mutual TLS.</summary>
    public MailBuilder ClientCert(string path, string? password = null)
    {
        _clientCertPath = path; _clientCertPassword = password; return this;
    }

    // ── SMTP (producer) ───────────────────────────────────────────────

    /// <summary>From address.</summary>
    public MailBuilder From(string address) { _from = address; return this; }
    /// <summary>From address from an expression.</summary>
    public MailBuilder From(IExpression address) { _from = address.ToTemplateString(); return this; }

    /// <summary>To address(es), comma-separated.</summary>
    public MailBuilder To(string addresses) { _to = addresses; return this; }
    /// <summary>To address(es) from an expression.</summary>
    public MailBuilder To(IExpression addresses) { _to = addresses.ToTemplateString(); return this; }

    /// <summary>CC address(es), comma-separated.</summary>
    public MailBuilder Cc(string addresses) { _cc = addresses; return this; }
    /// <summary>CC address(es) from an expression.</summary>
    public MailBuilder Cc(IExpression addresses) { _cc = addresses.ToTemplateString(); return this; }

    /// <summary>BCC address(es), comma-separated.</summary>
    public MailBuilder Bcc(string addresses) { _bcc = addresses; return this; }
    /// <summary>BCC address(es) from an expression.</summary>
    public MailBuilder Bcc(IExpression addresses) { _bcc = addresses.ToTemplateString(); return this; }

    /// <summary>Reply-To address.</summary>
    public MailBuilder ReplyTo(string address) { _replyTo = address; return this; }
    /// <summary>Reply-To address from an expression.</summary>
    public MailBuilder ReplyTo(IExpression address) { _replyTo = address.ToTemplateString(); return this; }

    /// <summary>Email subject.</summary>
    public MailBuilder Subject(string subject) { _subject = subject; return this; }
    /// <summary>Email subject from an expression.</summary>
    public MailBuilder Subject(IExpression subject) { _subject = subject.ToTemplateString(); return this; }

    /// <summary>Content type. Default "text/plain".</summary>
    public MailBuilder ContentType(string type) { _contentType = type; return this; }
    /// <summary>Content type from an expression.</summary>
    public MailBuilder ContentType(IExpression type) { _contentType = type.ToTemplateString(); return this; }

    /// <summary>Send body as alternative (e.g. HTML alternative to plain text).</summary>
    public MailBuilder AlternativeBody() { _alternativeBody = true; return this; }

    /// <summary>Attachment file paths, comma-separated.</summary>
    public MailBuilder Attachments(string paths) { _attachments = paths; return this; }

    // ── IMAP/POP3 (consumer) ──────────────────────────────────────────

    /// <summary>Mailbox folder. Default "INBOX".</summary>
    public MailBuilder Folder(string folder) { _folder = folder; return this; }

    /// <summary>Additional IMAP folders to monitor, comma-separated.</summary>
    public MailBuilder AdditionalFolders(string folders) { _additionalFolders = folders; return this; }

    /// <summary>Poll delay in milliseconds. Default 60000.</summary>
    public MailBuilder Delay(int ms) { _delay = ms; return this; }

    /// <summary>Initial delay before first poll. Default 0.</summary>
    public MailBuilder InitialDelay(int ms) { _initialDelay = ms; return this; }

    /// <summary>Fetch filter: Unseen, All, Recent, Flagged, Answered, Unanswered.</summary>
    public MailBuilder FetchFilter(string filter) { _fetchFilter = filter; return this; }

    /// <summary>Shortcut for FetchFilter("Unseen").</summary>
    public MailBuilder Unseen() { _fetchFilter = "Unseen"; return this; }

    /// <summary>Max messages per poll. Default 50.</summary>
    public MailBuilder MaxMessages(int max) { _maxMessages = max; return this; }

    /// <summary>Post-processing: None, MarkRead, Delete, Move, MarkReadAndMove, Flag.</summary>
    public MailBuilder PostProcess(string action) { _postProcess = action; return this; }

    /// <summary>Move processed messages to this folder (IMAP).</summary>
    public MailBuilder MoveTo(string folder) { _moveTo = folder; return this; }

    /// <summary>Sort order: None, DateAsc, DateDesc, SubjectAsc, FromAsc, SizeAsc, SizeDesc.</summary>
    public MailBuilder SortBy(string sort) { _sortBy = sort; return this; }

    /// <summary>Use IMAP IDLE for push notifications instead of polling.</summary>
    public MailBuilder Idle(int? timeoutMs = null) { _idle = true; _idleTimeout = timeoutMs; return this; }

    /// <summary>Fetch message body. Default true.</summary>
    public MailBuilder FetchBody(bool fetch = true) { _fetchBody = fetch; return this; }

    /// <summary>Fetch message attachments. Default true.</summary>
    public MailBuilder FetchAttachments(bool fetch = true) { _fetchAttachments = fetch; return this; }

    /// <summary>Max attachment size in bytes. 0 = unlimited.</summary>
    public MailBuilder MaxAttachmentSize(long bytes) { _maxAttachmentSize = bytes; return this; }

    /// <summary>Peek at messages without marking as read (IMAP).</summary>
    public MailBuilder Peek() { _peek = true; return this; }

    /// <summary>Enable idempotent consumer (skip already-processed messages).</summary>
    public MailBuilder Idempotent() { _idempotent = true; return this; }

    /// <summary>IMAP SEARCH query string.</summary>
    public MailBuilder SearchQuery(string query) { _searchQuery = query; return this; }

    /// <summary>Minimum message age in milliseconds.</summary>
    public MailBuilder MinAge(long ms) { _minAge = ms; return this; }

    /// <summary>Maximum message age in milliseconds.</summary>
    public MailBuilder MaxAge(long ms) { _maxAge = ms; return this; }

    /// <summary>Filter messages by subject (contains).</summary>
    public MailBuilder SubjectFilter(string filter) { _subjectFilter = filter; return this; }

    /// <summary>Filter messages by sender (contains).</summary>
    public MailBuilder FromFilter(string filter) { _fromFilter = filter; return this; }

    /// <summary>Map MIME headers to exchange headers.</summary>
    public MailBuilder MapMimeHeaders() { _mapMimeHeaders = true; return this; }

    /// <summary>Keep the raw MIME message in the exchange.</summary>
    public MailBuilder KeepRawMessage() { _keepRawMessage = true; return this; }

    /// <summary>Disconnect after each operation.</summary>
    public MailBuilder Disconnect() { _disconnect = true; return this; }

    // ── Build ─────────────────────────────────────────────────────────

    /// <summary>Builds the Mail endpoint URI string.</summary>
    public string Build()
    {
        var sb = new StringBuilder();
        sb.Append(_scheme);
        sb.Append(':');
        sb.Append(_server);

        var sep = '?';

        void Append(string key, string value)
        {
            sb.Append(sep); sb.Append(key); sb.Append('=');
            sb.Append(HttpUtility.UrlEncode(value)); sep = '&';
        }

        void AppendIf(string key, string? value) { if (!string.IsNullOrEmpty(value)) Append(key, value); }
        void AppendBool(string key, bool value) { if (value) Append(key, "true"); }
        void AppendBoolExplicit(string key, bool? value)
        {
            if (value.HasValue) Append(key, value.Value.ToString().ToLowerInvariant());
        }
        void AppendInt(string key, int? value) { if (value.HasValue) Append(key, value.Value.ToString()); }
        void AppendLong(string key, long? value) { if (value.HasValue) Append(key, value.Value.ToString()); }

        // Connection
        AppendInt("port", _port);
        AppendIf("security", _security);
        AppendInt("connectionTimeout", _connectionTimeout);
        AppendInt("timeout", _timeout);
        AppendIf("username", _username);
        AppendIf("password", _password);
        AppendIf("connectionFactory", _connectionFactory);
        AppendIf("accessToken", _accessToken);
        AppendIf("authMechanism", _authMechanism);
        AppendBool("skipCertificateValidation", _skipCertificateValidation);
        AppendIf("clientCertPath", _clientCertPath);
        AppendIf("clientCertPassword", _clientCertPassword);

        // SMTP
        AppendIf("from", _from);
        AppendIf("to", _to);
        AppendIf("cc", _cc);
        AppendIf("bcc", _bcc);
        AppendIf("replyTo", _replyTo);
        AppendIf("subject", _subject);
        AppendIf("contentType", _contentType);
        AppendBool("alternativeBody", _alternativeBody);
        AppendIf("attachments", _attachments);

        // IMAP/POP3
        AppendIf("folder", _folder);
        AppendIf("additionalFolders", _additionalFolders);
        AppendInt("delay", _delay);
        AppendInt("initialDelay", _initialDelay);
        AppendIf("fetchFilter", _fetchFilter);
        AppendInt("maxMessages", _maxMessages);
        AppendIf("postProcess", _postProcess);
        AppendIf("moveTo", _moveTo);
        AppendIf("sortBy", _sortBy);
        AppendBool("idle", _idle);
        AppendInt("idleTimeout", _idleTimeout);
        AppendBoolExplicit("fetchBody", _fetchBody);
        AppendBoolExplicit("fetchAttachments", _fetchAttachments);
        AppendLong("maxAttachmentSize", _maxAttachmentSize);
        AppendBool("peek", _peek);
        AppendBool("idempotent", _idempotent);
        AppendIf("searchQuery", _searchQuery);
        AppendLong("minAge", _minAge);
        AppendLong("maxAge", _maxAge);
        AppendIf("subjectFilter", _subjectFilter);
        AppendIf("fromFilter", _fromFilter);
        AppendBool("mapMimeHeaders", _mapMimeHeaders);
        AppendBool("keepRawMessage", _keepRawMessage);
        AppendBool("disconnect", _disconnect);

        return sb.ToString();
    }

    /// <summary>Implicit conversion to URI string.</summary>
    public static implicit operator string(MailBuilder b) => b.Build();

    /// <inheritdoc/>
    public override string ToString() => Build();
}

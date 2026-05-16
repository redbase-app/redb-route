namespace redb.Route.Mail;

/// <summary>
/// Header constants for email messages in the redb.Route pipeline.
/// All headers use the "redbMail." prefix.
/// </summary>
public static class MailHeaders
{
    /// <summary>Header prefix for all mail-related headers.</summary>
    public const string Prefix = "redbMail.";

    // ── Addressing ────────────────────────────────────────────────────

    /// <summary>Sender address (RFC 5322 From).</summary>
    public const string From = "redbMail.From";

    /// <summary>Comma-separated recipient addresses (To).</summary>
    public const string To = "redbMail.To";

    /// <summary>Comma-separated CC addresses.</summary>
    public const string Cc = "redbMail.Cc";

    /// <summary>Comma-separated BCC addresses.</summary>
    public const string Bcc = "redbMail.Bcc";

    /// <summary>Reply-To address.</summary>
    public const string ReplyTo = "redbMail.ReplyTo";

    /// <summary>Sender (envelope sender, may differ from From).</summary>
    public const string Sender = "redbMail.Sender";

    // ── Subject & identifiers ─────────────────────────────────────────

    /// <summary>Email subject line.</summary>
    public const string Subject = "redbMail.Subject";

    /// <summary>Message-ID header (RFC 2822).</summary>
    public const string MessageId = "redbMail.MessageId";

    /// <summary>In-Reply-To header (thread reference).</summary>
    public const string InReplyTo = "redbMail.InReplyTo";

    /// <summary>References header (full thread chain).</summary>
    public const string References = "redbMail.References";

    // ── Content ───────────────────────────────────────────────────────

    /// <summary>MIME content type of the body (text/plain, text/html, multipart/mixed).</summary>
    public const string ContentType = "redbMail.ContentType";

    /// <summary>Whether the email body is HTML.</summary>
    public const string IsHtml = "redbMail.IsHtml";

    /// <summary>Plain text body (when multipart, the text/plain alternative).</summary>
    public const string TextBody = "redbMail.TextBody";

    /// <summary>HTML body (when multipart, the text/html alternative).</summary>
    public const string HtmlBody = "redbMail.HtmlBody";

    // ── Attachments ───────────────────────────────────────────────────

    /// <summary>Number of attachments.</summary>
    public const string AttachmentCount = "redbMail.AttachmentCount";

    /// <summary>Whether the message has attachments.</summary>
    public const string HasAttachments = "redbMail.HasAttachments";

    /// <summary>Comma-separated attachment file names.</summary>
    public const string AttachmentNames = "redbMail.AttachmentNames";

    // ── Date & timestamps ─────────────────────────────────────────────

    /// <summary>Date header from the email.</summary>
    public const string Date = "redbMail.Date";

    /// <summary>Date the email was received (IMAP internal date).</summary>
    public const string ReceivedDate = "redbMail.ReceivedDate";

    // ── Flags & metadata ──────────────────────────────────────────────

    /// <summary>IMAP message UID.</summary>
    public const string Uid = "redbMail.Uid";

    /// <summary>IMAP/POP3 message index (sequence number).</summary>
    public const string Index = "redbMail.Index";

    /// <summary>IMAP folder name where the message was found.</summary>
    public const string Folder = "redbMail.Folder";

    /// <summary>IMAP message flags (Seen, Answered, Flagged, etc.).</summary>
    public const string Flags = "redbMail.Flags";

    /// <summary>Message size in bytes.</summary>
    public const string Size = "redbMail.Size";

    /// <summary>Message priority (Urgent, Normal, NonUrgent).</summary>
    public const string Priority = "redbMail.Priority";

    // ── Protocol info ─────────────────────────────────────────────────

    /// <summary>Protocol used to retrieve the message (imap, pop3).</summary>
    public const string Protocol = "redbMail.Protocol";

    /// <summary>Whether the connection used TLS/SSL.</summary>
    public const string Secure = "redbMail.Secure";

    /// <summary>
    /// Checks whether a header key belongs to the redbMail namespace.
    /// </summary>
    public static bool IsRedbHeader(string key) => key.StartsWith(Prefix, StringComparison.Ordinal);
}

using MailKit;
using Microsoft.Extensions.DependencyInjection;
using MimeKit;
using redb.Route.Core;

namespace redb.Route.Mail;

/// <summary>
/// Shared utility for converting MimeMessage to Exchange.
/// Used by both ImapConsumer and Pop3Consumer.
/// </summary>
internal static class MailMessageHelper
{
    /// <summary>
    /// Creates an Exchange from a MimeMessage, populating all MailHeaders and body/attachments.
    /// </summary>
    /// <param name="mime">Source MIME message.</param>
    /// <param name="protocol">Protocol name: "imap" or "pop3".</param>
    /// <param name="folder">Folder name (IMAP) or null (POP3).</param>
    /// <param name="uid">IMAP UniqueId or POP3 index. Pass default for POP3.</param>
    /// <param name="index">Sequence index of the message.</param>
    /// <summary>
    /// Builds a headers-only <see cref="MimeMessage"/> from a fetched header list by round-tripping
    /// it through the MIME parser — copying headers into a fresh message does NOT sync properties
    /// like Subject/From, the parser is the one construction path that does (fetchBody=false).
    /// </summary>
    internal static MimeMessage FromHeadersOnly(HeaderList headerList)
    {
        using var ms = new MemoryStream();
        headerList.WriteTo(ms);
        ms.Write("\r\n"u8); // blank line: end of headers, empty body
        ms.Position = 0;
        var message = MimeMessage.Load(ms);
        message.Body = null; // headers-only: the parser materializes an empty text part otherwise
        return message;
    }

    public static Exchange CreateExchange(
        MimeMessage mime,
        string protocol,
        string? folder = null,
        UniqueId uid = default,
        int index = -1,
        IServiceScopeFactory? scopeFactory = null,
        MailEndpointOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(mime);

        var body = mime.HtmlBody ?? mime.TextBody ?? "";
        var message = new Message(body);
        var headers = message.Headers;

        // ── Addressing ───────────────────────────────────────────────
        SetIfNotEmpty(headers, MailHeaders.From, FormatAddresses(mime.From));
        SetIfNotEmpty(headers, MailHeaders.To, FormatAddresses(mime.To));
        SetIfNotEmpty(headers, MailHeaders.Cc, FormatAddresses(mime.Cc));
        SetIfNotEmpty(headers, MailHeaders.Bcc, FormatAddresses(mime.Bcc));
        SetIfNotEmpty(headers, MailHeaders.ReplyTo, FormatAddresses(mime.ReplyTo));
        if (mime.Sender is not null)
            headers[MailHeaders.Sender] = mime.Sender.Address;

        // ── Subject & identifiers ────────────────────────────────────
        SetIfNotEmpty(headers, MailHeaders.Subject, mime.Subject);
        SetIfNotEmpty(headers, MailHeaders.MessageId, mime.MessageId);
        SetIfNotEmpty(headers, MailHeaders.InReplyTo, mime.InReplyTo);
        if (mime.References.Count > 0)
            headers[MailHeaders.References] = string.Join(" ", mime.References);

        // ── Content ──────────────────────────────────────────────────
        headers[MailHeaders.IsHtml] = mime.HtmlBody is not null;
        if (mime.TextBody is not null)
            headers[MailHeaders.TextBody] = mime.TextBody;
        if (mime.HtmlBody is not null)
            headers[MailHeaders.HtmlBody] = mime.HtmlBody;
        if (mime.Body is not null)
            headers[MailHeaders.ContentType] = mime.Body.ContentType.MimeType;

        // ── Attachments ──────────────────────────────────────────────
        var attachments = mime.Attachments.ToList();
        headers[MailHeaders.AttachmentCount] = attachments.Count;
        headers[MailHeaders.HasAttachments] = attachments.Count > 0;

        if (attachments.Count > 0)
        {
            var names = new List<string>();
            var mailAttachments = new List<MailAttachment>();

            // fetchAttachments=false: the metadata (count, names) stays, the payload copies do
            // not. maxAttachmentSize caps the DECODED copy handed to the route - the MIME message
            // itself is already in memory at this point, so the cap bounds the exchange, not the
            // network transfer (часть B of the options sweep; both were dead options before).
            var fetchAttachments = options?.FetchAttachments ?? true;
            var maxSize = options?.MaxAttachmentSize ?? 0;

            foreach (var att in attachments)
            {
                var fileName = att.ContentDisposition?.FileName
                    ?? att.ContentType.Name
                    ?? "attachment";
                names.Add(fileName);

                if (!fetchAttachments)
                    continue;

                if (att is MimePart part && part.Content is not null)
                {
                    using var ms = new MemoryStream();
                    part.Content.DecodeTo(ms);
                    if (maxSize > 0 && ms.Length > maxSize)
                        continue; // oversized: named in AttachmentNames, payload not carried
                    mailAttachments.Add(new MailAttachment(
                        fileName, ms.ToArray(), part.ContentType.MimeType));
                }
            }

            headers[MailHeaders.AttachmentNames] = string.Join(", ", names);
            // Body-type contract (ревью дуги, M17): with fetchAttachments on (the default), a mail
            // that HAS attachments always yields a MailMessageBody - even when none of them
            // decoded into the list (message/rfc822 parts, everything over the size cap).
            // Existing routes cast on HasAttachments=true; handing them a bare string only for
            // those shapes was an unannounced contract change. fetchAttachments=false is the
            // explicit opt-in for "metadata only, plain body".
            if (fetchAttachments)
                message.Body = new MailMessageBody(body, mailAttachments);
        }

        // ── Dates ────────────────────────────────────────────────────
        if (mime.Date != default)
            headers[MailHeaders.Date] = mime.Date.ToString("O");

        // ── Priority ─────────────────────────────────────────────────
        headers[MailHeaders.Priority] = mime.Importance switch
        {
            MessageImportance.High => "high",
            MessageImportance.Low => "low",
            _ => "normal"
        };

        // ── Protocol ─────────────────────────────────────────────────
        headers[MailHeaders.Protocol] = protocol;

        // mapMimeHeaders=true: every raw MIME header rides the exchange under redbMail.Mime.*
        // (a dead option before часть B of the options sweep).
        if (options?.MapMimeHeaders == true)
        {
            foreach (var h in mime.Headers)
                headers[MailHeaders.MimePrefix + h.Field] = h.Value;
        }

        // ── IMAP-specific ────────────────────────────────────────────
        if (folder is not null)
            headers[MailHeaders.Folder] = folder;

        if (uid.IsValid)
            headers[MailHeaders.Uid] = uid.Id.ToString();

        if (index >= 0)
            headers[MailHeaders.Index] = index;

        return Exchange.Create(message, scopeFactory);
    }

    private static string FormatAddresses(InternetAddressList list)
    {
        if (list.Count == 0) return "";
        return string.Join(", ", list.Mailboxes.Select(m => m.Address));
    }

    private static void SetIfNotEmpty(IDictionary<string, object?> headers, string key, string? value)
    {
        if (!string.IsNullOrEmpty(value))
            headers[key] = value;
    }
}

/// <summary>
/// Composite body for emails with attachments.
/// Text holds the email body, Attachments holds the decoded file data.
/// </summary>
public sealed class MailMessageBody(string text, IReadOnlyList<MailAttachment> attachments) : ICloneable
{
    /// <summary>Decoded text body.</summary>
    public string Text { get; } = text;

    /// <summary>Decoded attachments that fit the size cap (names always ride the headers).</summary>
    public IReadOnlyList<MailAttachment> Attachments { get; } = attachments;

    // A class, not a record: records may not implement ICloneable (CS8859), and ICloneable is
    // what Message.DeepCopyBody requires to carry this body through a checkpoint snapshot.
    /// <summary>Deep copy for checkpoint snapshots — attachment payload bytes must not be shared.</summary>
    public object Clone() => new MailMessageBody(
        Text, Attachments.Select(a => (MailAttachment)a.Clone()).ToList());
}

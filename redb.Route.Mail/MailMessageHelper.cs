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
    public static Exchange CreateExchange(
        MimeMessage mime,
        string protocol,
        string? folder = null,
        UniqueId uid = default,
        int index = -1,
        IServiceScopeFactory? scopeFactory = null)
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

            foreach (var att in attachments)
            {
                var fileName = att.ContentDisposition?.FileName
                    ?? att.ContentType.Name
                    ?? "attachment";
                names.Add(fileName);

                if (att is MimePart part && part.Content is not null)
                {
                    using var ms = new MemoryStream();
                    part.Content.DecodeTo(ms);
                    mailAttachments.Add(new MailAttachment(
                        fileName, ms.ToArray(), part.ContentType.MimeType));
                }
            }

            headers[MailHeaders.AttachmentNames] = string.Join(", ", names);
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
public sealed record MailMessageBody(string Text, IReadOnlyList<MailAttachment> Attachments);

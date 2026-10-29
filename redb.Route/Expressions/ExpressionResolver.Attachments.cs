using System.Text;
using redb.Route.Abstractions;

namespace redb.Route.Expressions;

/// <summary>
/// Partial class ExpressionResolver: the message attachments in the language, with the names of Camel Simple.
/// <c>attachments</c> is the list of the In message (<c>attachments.size</c>, <c>attachments[0].fileName</c>);
/// <c>attachmentContent(name)</c>, <c>attachmentContentAsText(name)</c>, <c>attachmentContentType(name)</c> and
/// <c>attachmentHeader(name, header)</c> read the first attachment with that name, <c>null</c> when there is none.
/// </summary>
/// <remarks>
/// Expressions are evaluated synchronously, so the content functions read the whole content synchronously: they suit
/// small parts (a text note, a short XML). Large files are for the attachment steps and the connectors.
/// </remarks>
public static partial class ExpressionResolver
{
    private const string AttachmentsRoot = "attachments";

    /// <summary>Whether <paramref name="name"/> is the <c>attachments</c> root or a path under it.</summary>
    private static bool IsAttachmentsPath(string name) =>
        name.StartsWith(AttachmentsRoot, StringComparison.Ordinal)
        && (name.Length == AttachmentsRoot.Length || name[AttachmentsRoot.Length] is '.' or '[');

    /// <summary>
    /// Resolves <c>attachments</c> and a path under it: <c>.size</c> / <c>.count</c>, an index <c>[i]</c>, and the
    /// members of an attachment (<c>name</c>, <c>fileName</c>, <c>contentType</c>, <c>contentId</c>, <c>length</c>).
    /// </summary>
    private static object? ResolveAttachmentsPath(IExchange exchange, string name)
    {
        var attachments = exchange.In.Attachments;
        var rest = name[AttachmentsRoot.Length..];
        if (rest.Length == 0)
            return attachments;
        if (rest is ".size" or ".count" or ".Size" or ".Count")
            return attachments.Count;
        return ResolvePropertyPath(attachments.ToArray(), rest.TrimStart('.'));
    }

    private static Attachment? FirstAttachment(object? name, IExchange exchange, string function)
    {
        var text = name?.ToString();
        if (string.IsNullOrEmpty(text))
            throw new InvalidOperationException($"{function}() needs the name of an attachment.");
        return exchange.In.Attachments.Find(text);
    }

    /// <summary><c>attachmentContent(name)</c>: the content of the first attachment with that name as bytes, or <c>null</c>.</summary>
    public static object? Ast_AttachmentContent(object? name, IExchange exchange) =>
        FirstAttachment(name, exchange, "attachmentContent")?.Content.ReadAllBytes();

    /// <summary>
    /// <c>attachmentContentAsText(name)</c>: the content of the first attachment with that name as text, decoded with
    /// the <c>charset</c> of its content type (UTF-8 when it has none), or <c>null</c>.
    /// </summary>
    public static object? Ast_AttachmentContentAsText(object? name, IExchange exchange)
    {
        var attachment = FirstAttachment(name, exchange, "attachmentContentAsText");
        return attachment is null ? null : CharsetOf(attachment.ContentType).GetString(attachment.Content.ReadAllBytes());
    }

    /// <summary><c>attachmentContentType(name)</c>: the content type of the first attachment with that name, or <c>null</c>.</summary>
    public static object? Ast_AttachmentContentType(object? name, IExchange exchange) =>
        FirstAttachment(name, exchange, "attachmentContentType")?.ContentType;

    /// <summary>
    /// <c>attachmentHeader(name, header)</c>: a MIME header of the first attachment with that name (case-insensitive),
    /// or <c>null</c>.
    /// </summary>
    public static object? Ast_AttachmentHeader(object? name, object? header, IExchange exchange)
    {
        var headerName = header?.ToString();
        if (string.IsNullOrEmpty(headerName))
            throw new InvalidOperationException("attachmentHeader() needs the name of an attachment and the name of a header.");
        var attachment = FirstAttachment(name, exchange, "attachmentHeader");
        return attachment is not null && attachment.Headers.TryGetValue(headerName, out var value) ? value : null;
    }

    private static Encoding CharsetOf(string contentType)
    {
        if (System.Net.Http.Headers.MediaTypeHeaderValue.TryParse(contentType, out var parsed)
            && parsed.CharSet is { Length: > 0 } charset)
        {
            try
            {
                return Encoding.GetEncoding(charset.Trim('"'));
            }
            catch (ArgumentException ex)
            {
                throw new InvalidOperationException(
                    $"attachmentContentAsText(): the attachment declares charset '{charset}', which this runtime does not know.", ex);
            }
        }
        return Encoding.UTF8;
    }
}

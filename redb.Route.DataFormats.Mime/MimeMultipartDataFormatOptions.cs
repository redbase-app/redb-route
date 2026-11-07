using System.Text;

namespace redb.Route.DataFormats.Mime;

/// <summary>
/// Options of <see cref="MimeMultipartDataFormat"/>. The limits hold wherever the body comes from (HTTP, a file, a queue),
/// not only behind a web server.
/// </summary>
public sealed class MimeMultipartDataFormatOptions
{
    /// <summary>
    /// The subtype <c>Marshal</c> writes and the format registers under: <c>form-data</c> (the default: a
    /// <c>FormBody</c> body and file attachments) or <c>mixed</c> (the body as the first part, as Camel's
    /// <c>mimeMultipart</c>). <c>Unmarshal</c> reads <c>multipart/form-data</c> as a form and any other
    /// <c>multipart/*</c> the Camel way, whatever this says.
    /// </summary>
    public string SubType { get; set; } = "form-data";

    /// <summary>
    /// Marshal with the <c>mixed</c> subtype writes a multipart even when the message has no attachments (Camel's
    /// <c>multipartWithoutAttachment</c>); otherwise such a body is left as it is. Default <c>false</c>.
    /// </summary>
    public bool MultipartWithoutAttachment { get; set; }

    /// <summary>Most parts one body may have. Default 1000.</summary>
    public int MaxParts { get; set; } = 1000;

    /// <summary>Largest decoded part, in bytes. Default 10 MB.</summary>
    public long MaxPartBytes { get; set; } = 10L * 1024 * 1024;

    /// <summary>Largest body, in bytes as it arrives. Default 32 MB.</summary>
    public long MaxTotalBytes { get; set; } = 32L * 1024 * 1024;

    /// <summary>Longest file name of a part, in characters. Default 255.</summary>
    public int MaxFileNameLength { get; set; } = 255;

    /// <summary>
    /// Encoding of form fields and headers that declare none. Default UTF-8, what browsers send from a UTF-8 page and
    /// what RFC 7578 recommends.
    /// </summary>
    public Encoding DefaultCharset { get; set; } = Encoding.UTF8;
}

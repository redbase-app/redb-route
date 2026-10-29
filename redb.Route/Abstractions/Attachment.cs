namespace redb.Route.Abstractions;

/// <summary>
/// One attachment of a message (Apache Camel's <c>Attachment</c>): a named piece of content that travels beside the body,
/// such as a file of a <c>multipart/form-data</c> upload, a mail attachment or an MTOM part. Immutable: replace it on the
/// message with <see cref="IMessageAttachments.Set"/>.
/// </summary>
/// <remarks>
/// The text fields end up in MIME headers when a producer writes the attachment, so a carriage return or line feed in
/// any of them is rejected here rather than written onto the wire.
/// </remarks>
public sealed class Attachment
{
    private static readonly IReadOnlyDictionary<string, string> NoHeaders =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    private readonly string _name = null!;
    private readonly string? _fileName;
    private readonly string _contentType = DefaultContentType;
    private readonly string? _contentId;
    private readonly IReadOnlyDictionary<string, string> _headers = NoHeaders;

    /// <summary>Content type of an attachment that does not declare one.</summary>
    public const string DefaultContentType = "application/octet-stream";

    /// <summary>
    /// The key the message finds the attachment by: the file name, the form field name or the <c>Content-ID</c>,
    /// whichever the source of the attachment uses. Names may repeat on one message.
    /// </summary>
    public required string Name
    {
        get => _name;
        init => _name = RequireText(value, nameof(Name));
    }

    /// <summary>Original file name, without a directory, when the attachment came as a file.</summary>
    public string? FileName
    {
        get => _fileName;
        init => _fileName = value is null ? null : RequireText(value, nameof(FileName));
    }

    /// <summary>MIME type of the content; <see cref="DefaultContentType"/> when not set.</summary>
    public string ContentType
    {
        get => _contentType;
        init => _contentType = RequireText(value, nameof(ContentType));
    }

    /// <summary>MIME <c>Content-ID</c> without the angle brackets, for parts referenced as <c>cid:</c>.</summary>
    public string? ContentId
    {
        get => _contentId;
        init => _contentId = value is null ? null : RequireText(value, nameof(ContentId));
    }

    /// <summary>Headers of the MIME part the attachment came from; empty when it did not come from one.</summary>
    public IReadOnlyDictionary<string, string> Headers
    {
        get => _headers;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            var copy = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, text) in value)
                copy[RequireText(key, nameof(Headers))] = RequireText(text, nameof(Headers), allowEmpty: true);
            _headers = copy;
        }
    }

    /// <summary>The content, readable as often as needed.</summary>
    public required AttachmentContent Content { get; init; }

    /// <summary>Length of the content in bytes (<c>attachments[0].length</c> in an expression).</summary>
    public long Length => Content.Length;

    /// <summary>A copy of this attachment with other content (the metadata stays).</summary>
    public Attachment WithContent(AttachmentContent content)
    {
        ArgumentNullException.ThrowIfNull(content);
        return new Attachment
        {
            Name = Name,
            FileName = FileName,
            ContentType = ContentType,
            ContentId = ContentId,
            Headers = Headers,
            Content = content
        };
    }

    private static string RequireText(string? value, string field, bool allowEmpty = false)
    {
        if (value is null || (!allowEmpty && value.Length == 0))
            throw new ArgumentException($"Attachment {field} must not be empty.", field);
        if (value.AsSpan().IndexOfAny('\r', '\n') >= 0)
            throw new ArgumentException($"Attachment {field} must not contain a line break.", field);
        return value;
    }
}

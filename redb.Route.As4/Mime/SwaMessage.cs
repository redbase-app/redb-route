using System.Security.Cryptography;
using System.Xml;
using MimeKit;
using redb.Route.Configuration;
using redb.Route.Core;

namespace redb.Route.As4.Mime;

/// <summary>
/// An AS4 message as it travels: the SOAP 1.2 envelope and the MIME parts it refers to by Content-ID
/// (SOAP with Attachments). A message without attachments may arrive as a bare envelope, not MIME.
/// <para>
/// Part contents are streams the message owns: small ones in memory, large ones spooled to a temporary file by the
/// core <see cref="StreamCache"/> (deleted when the part is disposed). Every processing step — decryption,
/// decompression, compression, encryption — reads one stream and writes the next, so a large payload is never held
/// in a <c>byte[]</c>. Disposing the message disposes every part it still owns; a part handed to the route is
/// detached first (<see cref="SwaPart.Detach"/>).
/// </para>
/// </summary>
internal sealed class SwaMessage : IDisposable
{
    private readonly Stream? _source;

    private SwaMessage(XmlDocument envelope, Dictionary<string, SwaPart> parts, StreamCacheOptions spool, Stream? source)
    {
        Envelope = envelope;
        Parts = parts;
        Spool = spool;
        _source = source;
    }

    /// <summary>The SOAP envelope, loaded with whitespace preserved (signature canonicalization depends on it).</summary>
    public XmlDocument Envelope { get; }

    /// <summary>The attachments, keyed by Content-ID without angle brackets.</summary>
    public Dictionary<string, SwaPart> Parts { get; }

    /// <summary>Where processing steps spool part contents that outgrow memory.</summary>
    public StreamCacheOptions Spool { get; }

    /// <summary>
    /// Reads a message from an HTTP body and its Content-Type. <paramref name="body"/> must be seekable; the message
    /// takes ownership of it (MimeKit parses it persistently, so parts are read from it without a copy until they are
    /// decoded). The envelope is parsed with <see cref="SafeXml"/> (no DTD, bounded by <paramref name="maxEnvelopeCharacters"/>).
    /// </summary>
    public static SwaMessage Read(string contentType, Stream body, long maxEnvelopeCharacters, StreamCacheOptions spool)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(spool);
        if (!body.CanSeek)
            throw new ArgumentException("The message body must be seekable.", nameof(body));

        var parts = new Dictionary<string, SwaPart>(StringComparer.Ordinal);
        try
        {
            var type = ContentType.Parse(contentType);
            if (!type.IsMimeType("multipart", "related"))
                return new SwaMessage(SafeXml.LoadDocument(ReadAll(body), preserveWhitespace: true, maxEnvelopeCharacters), parts, spool, body);

            // The HTTP Content-Type stands in for the entity headers; persistent parsing keeps the parts as views of the body.
            body.Position = 0;
            using var entity = new MimeKit.IO.ChainedStream();
            entity.Add(new MemoryStream(System.Text.Encoding.ASCII.GetBytes("Content-Type: " + contentType + "\r\n\r\n")), leaveOpen: false);
            entity.Add(body, leaveOpen: true);
            if (new MimeParser(entity, MimeFormat.Entity, persistent: true).ParseEntity() is not Multipart multipart || multipart.Count == 0)
                throw new FormatException("The multipart/related body has no parts.");

            var start = type.Parameters["start"]?.Trim('<', '>');
            var root = (start is null ? null : multipart.OfType<MimePart>().FirstOrDefault(p => p.ContentId == start))
                ?? multipart[0] as MimePart
                ?? throw new FormatException("The root part of the multipart/related body is not a leaf part.");

            byte[] envelopeBytes;
            using (var envelopeStream = new MemoryStream())
            {
                Content(root).DecodeTo(envelopeStream);
                envelopeBytes = envelopeStream.ToArray();
            }
            var envelope = SafeXml.LoadDocument(envelopeBytes, preserveWhitespace: true, maxEnvelopeCharacters);

            foreach (var part in multipart.OfType<MimePart>())
            {
                if (ReferenceEquals(part, root)) continue;
                if (string.IsNullOrEmpty(part.ContentId))
                    throw new FormatException("An attachment part has no Content-ID.");
                if (parts.ContainsKey(part.ContentId))
                    throw new FormatException($"Two parts carry the Content-ID '{part.ContentId}'.");

                var decoded = new StreamCache(spool);
                try
                {
                    Content(part).DecodeTo(decoded);
                    decoded.CompleteWriting();
                }
                catch
                {
                    decoded.Dispose();
                    throw;
                }
                parts.Add(part.ContentId, new SwaPart(part.ContentId, part.ContentType.MimeType, decoded));
            }

            return new SwaMessage(envelope, parts, spool, body);
        }
        catch
        {
            foreach (var part in parts.Values) part.Dispose();
            throw;
        }
    }

    /// <summary>Reads a message held in memory (signals, tests); see <see cref="Read(string, Stream, long, StreamCacheOptions)"/>.</summary>
    public static SwaMessage Read(string contentType, byte[] body, long maxEnvelopeCharacters) =>
        Read(contentType, new MemoryStream(body ?? throw new ArgumentNullException(nameof(body)), writable: false),
            maxEnvelopeCharacters, new StreamCacheOptions());

    /// <summary>A message to send: <paramref name="envelope"/> and no attachments yet.</summary>
    public static SwaMessage Create(XmlDocument envelope, StreamCacheOptions? spool = null) =>
        new(envelope, new(StringComparer.Ordinal), spool ?? new StreamCacheOptions(), null);

    /// <summary>Adds an attachment the message then owns; throws on a repeated Content-ID.</summary>
    public SwaPart AddPart(string contentId, string contentType, Stream content)
    {
        var part = new SwaPart(contentId, contentType, content);
        if (!Parts.TryAdd(contentId, part))
            throw new InvalidOperationException($"The message already has a part with Content-ID '{contentId}'.");
        return part;
    }

    /// <summary>Adds an attachment held in memory; throws on a repeated Content-ID.</summary>
    public SwaPart AddPart(string contentId, string contentType, byte[] content) =>
        AddPart(contentId, contentType, new MemoryStream(content ?? throw new ArgumentNullException(nameof(content)), writable: false));

    /// <summary>
    /// Serializes the message for HTTP into <paramref name="output"/>: <c>multipart/related; type="application/soap+xml"</c>
    /// with the envelope as the start part and binary parts, or a bare SOAP 1.2 envelope when there are no attachments.
    /// Returns the Content-Type. Part contents are copied from their streams, not buffered.
    /// </summary>
    public string WriteTo(Stream output)
    {
        ArgumentNullException.ThrowIfNull(output);
        var envelopeBytes = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(Envelope.OuterXml);
        if (Parts.Count == 0)
        {
            output.Write(envelopeBytes);
            return "application/soap+xml; charset=UTF-8";
        }

        var rootId = "root." + Guid.NewGuid().ToString("N") + "@redb.route";
        var multipart = new Multipart("related");
        multipart.ContentType.Parameters.Add("type", "application/soap+xml");
        multipart.ContentType.Parameters.Add("start", "<" + rootId + ">");

        var root = new MimePart("application", "soap+xml")
        {
            ContentId = rootId,
            ContentTransferEncoding = ContentEncoding.Binary,
            Content = new MimeContent(new MemoryStream(envelopeBytes)),
        };
        root.ContentType.Charset = "UTF-8";
        multipart.Add(root);

        foreach (var part in Parts.Values)
        {
            multipart.Add(new MimePart(MimeKit.ContentType.Parse(part.ContentType))
            {
                ContentId = part.ContentId,
                ContentTransferEncoding = ContentEncoding.Binary,
                Content = new MimeContent(part.OpenRead()),
            });
        }

        multipart.WriteTo(FormatOptions.Default, output, contentOnly: true);
        // The header value, not ContentType.ToString(), which renders the whole "Content-Type:" line (the AS2 lesson).
        return multipart.Headers[HeaderId.ContentType]
            ?? throw new InvalidOperationException("The multipart body has no Content-Type header.");
    }

    /// <summary>Serializes the message into memory (signals, tests); see <see cref="WriteTo"/>.</summary>
    public (string ContentType, byte[] Body) Write()
    {
        using var output = new MemoryStream();
        var contentType = WriteTo(output);
        return (contentType, output.ToArray());
    }

    /// <summary>Runs one processing step into a new spool of this message; see <see cref="As4Spool.Run"/>.</summary>
    public Stream Spooled(Action<Stream> write) => As4Spool.Run(Spool, write);

    /// <summary>Disposes every part the message still owns and the body it was read from.</summary>
    public void Dispose()
    {
        foreach (var part in Parts.Values) part.Dispose();
        _source?.Dispose();
    }

    private static MimeContent Content(MimePart part) =>
        part.Content as MimeContent ?? throw new FormatException($"The MIME part '{part.ContentId}' has no content.");

    private static byte[] ReadAll(Stream stream)
    {
        stream.Position = 0;
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}

/// <summary>
/// One attachment: its Content-ID, media type and content. The content is a readable, seekable stream the part owns
/// until <see cref="Replace"/> swaps it for the output of a processing step or <see cref="Detach"/> hands it on.
/// </summary>
internal sealed class SwaPart : IDisposable
{
    private Stream? _content;

    public SwaPart(string contentId, string contentType, Stream content)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (!content.CanRead || !content.CanSeek)
            throw new ArgumentException("Part content must be readable and seekable.", nameof(content));
        ContentId = contentId;
        ContentType = contentType;
        _content = content;
    }

    /// <summary>A part held in memory (signals, tests).</summary>
    public SwaPart(string contentId, string contentType, byte[] content)
        : this(contentId, contentType, new MemoryStream(content ?? throw new ArgumentNullException(nameof(content)), writable: false))
    {
    }

    /// <summary>Content-ID without angle brackets.</summary>
    public string ContentId { get; }

    /// <summary>Media type of the part as it is now (after decryption: the type the ciphertext stood for).</summary>
    public string ContentType { get; set; }

    /// <summary>Length of the content in bytes.</summary>
    public long Length => Content.Length;

    private Stream Content => _content ?? throw new ObjectDisposedException(nameof(SwaPart), $"The content of part '{ContentId}' was handed on or released.");

    /// <summary>The content from its start. Reading it moves the shared position; each caller rewinds through this method.</summary>
    public Stream OpenRead()
    {
        var content = Content;
        content.Position = 0;
        return content;
    }

    /// <summary>SHA-256 of the content, read as a stream.</summary>
    public byte[] Sha256() => SHA256.HashData(OpenRead());

    /// <summary>The content as bytes. For small contents and tests: it loads the whole part.</summary>
    public byte[] ToArray()
    {
        using var buffer = new MemoryStream();
        OpenRead().CopyTo(buffer);
        return buffer.ToArray();
    }

    /// <summary>Makes <paramref name="next"/> the content and disposes the one it replaces.</summary>
    public void Replace(Stream next)
    {
        ArgumentNullException.ThrowIfNull(next);
        if (!next.CanRead || !next.CanSeek)
            throw new ArgumentException("Part content must be readable and seekable.", nameof(next));
        var previous = _content;
        _content = next;
        previous?.Dispose();
    }

    /// <summary>Hands the content, rewound, to a new owner; the part no longer disposes it.</summary>
    public Stream Detach()
    {
        var content = OpenRead();
        _content = null;
        return content;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _content?.Dispose();
        _content = null;
    }
}

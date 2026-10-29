using System.Text;
using System.Text.RegularExpressions;
using MimeKit;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Extensions;
using redb.Route.Serialization;
using MimeContentType = MimeKit.ContentType;

namespace redb.Route.DataFormats.Mime;

/// <summary>
/// MIME multipart as a data format, Apache Camel's <c>mimeMultipart</c> with a form mode added. It works on the whole
/// message (<see cref="IMessageDataFormat"/>): the boundary comes from the content type, the files become message
/// attachments.
/// </summary>
/// <remarks>
/// <para><b>Unmarshal.</b> A body that is not <c>multipart/*</c> is left as it is, as Camel does.
/// <c>multipart/form-data</c> (RFC 7578): the fields become a <see cref="FormBody"/> body, in order with repeated names,
/// and every part with a file name becomes an attachment named by that file name (as Camel Jetty names uploads), its
/// part headers kept (the form field name is in <c>Content-Disposition</c>). Any other <c>multipart/*</c>: the first part
/// becomes the body with its content type, the others attachments named by file name, then <c>Content-ID</c>, then a
/// generated id. File names lose their directory and line breaks. Large parts spool to a <see cref="StreamCache"/>
/// under the engine's stream cache options; the content type is removed (the form) or set to the first part's.</para>
/// <para><b>Marshal.</b> <c>form-data</c>: a <see cref="FormBody"/> (or string pairs, or no body) and the attachments as
/// files. <c>mixed</c>: the body as the first part and the attachments after it; without attachments the body is left
/// as it is unless <see cref="MimeMultipartDataFormatOptions.MultipartWithoutAttachment"/>. A new boundary per message,
/// written into the content type and the <c>Content-Type</c> header; the attachments stay on the message.</para>
/// </remarks>
public sealed class MimeMultipartDataFormat : IMessageDataFormat
{
    /// <summary>The content type the format registers under by default.</summary>
    public const string FormDataContentType = "multipart/form-data";

    private static readonly Regex Folding = new(@"\r?\n[ \t]*", RegexOptions.Compiled);
    private static readonly Regex QuotedFileName = new(@"(?:^|;)\s*filename\s*=\s*""([^""]*)""", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly MimeMultipartDataFormatOptions _options;
    private readonly ParserOptions _parserOptions;

    /// <summary>A format with the given options (defaults when <c>null</c>).</summary>
    public MimeMultipartDataFormat(MimeMultipartDataFormatOptions? options = null)
    {
        _options = options ?? new MimeMultipartDataFormatOptions();
        if (_options.SubType is not ("form-data" or "mixed"))
            throw new ArgumentException($"MIME multipart: SubType '{_options.SubType}' is not supported; use 'form-data' or 'mixed'.", nameof(options));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_options.MaxParts, nameof(_options.MaxParts));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_options.MaxPartBytes, nameof(_options.MaxPartBytes));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_options.MaxTotalBytes, nameof(_options.MaxTotalBytes));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_options.MaxFileNameLength, nameof(_options.MaxFileNameLength));
        ArgumentNullException.ThrowIfNull(_options.DefaultCharset, nameof(_options.DefaultCharset));

        _parserOptions = ParserOptions.Default.Clone();
        _parserOptions.CharsetEncoding = _options.DefaultCharset;
        _parserOptions.ParameterComplianceMode = RfcComplianceMode.Loose;
    }

    /// <inheritdoc />
    public string ContentType => "multipart/" + _options.SubType;

    // ── The byte contract: multipart needs the message (attachments, the content type) ──

    /// <inheritdoc />
    public byte[] Serialize<T>(T value) => throw NeedsTheMessage();

    /// <inheritdoc />
    public T? Deserialize<T>(byte[] data) => throw NeedsTheMessage();

    /// <inheritdoc />
    public object? Deserialize(byte[] data, Type type) => throw NeedsTheMessage();

    private static NotSupportedException NeedsTheMessage() => new(
        "MIME multipart works on the whole message (its content type and attachments): use the Marshal / Unmarshal / " +
        "ConvertBody steps, not the byte methods of the serializer.");

    // ── Unmarshal ──

    /// <inheritdoc />
    public async ValueTask UnmarshalAsync(IExchange exchange, Type targetType, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(exchange);
        ArgumentNullException.ThrowIfNull(targetType);
        var message = exchange.In;
        var declared = message.ContentType ?? message.GetHeader<string>("Content-Type");
        if (declared is null
            || !MimeContentType.TryParse(_parserOptions, declared, out var contentType)
            || !contentType.IsMimeType("multipart", "*"))
            return;

        var input = message.Body switch
        {
            byte[] bytes => new MemoryStream(bytes, writable: false),
            string text => new MemoryStream(Encoding.UTF8.GetBytes(text), writable: false),
            Stream stream => stream,
            _ => null
        };
        if (input is null)
            return;

        if (string.IsNullOrEmpty(contentType.Boundary))
        {
            var buffered = new MemoryStream();
            await new LimitedReadStream(input, _options.MaxTotalBytes).CopyToAsync(buffered, ct).ConfigureAwait(false);
            contentType.Boundary = DetectBoundary(buffered.GetBuffer().AsSpan(0, (int)buffered.Length));
            buffered.Position = 0;
            input = buffered;
        }

        MimeEntity entity;
        try
        {
            entity = await MimeEntity.LoadAsync(_parserOptions, contentType, new LimitedReadStream(input, _options.MaxTotalBytes), ct)
                .ConfigureAwait(false);
        }
        catch (FormatException ex)
        {
            throw new InvalidDataException($"MIME multipart: the body is not a valid {contentType.MimeType}: {ex.Message}", ex);
        }
        if (entity is not Multipart multipart)
            throw new InvalidDataException($"MIME multipart: the body declared as {contentType.MimeType} has no parts.");
        if (multipart.Count > _options.MaxParts)
            throw new InvalidDataException(
                $"MIME multipart: the body has {multipart.Count} parts, more than {_options.MaxParts} (MaxParts).");

        var created = new List<AttachmentContent>();
        try
        {
            if (contentType.IsMimeType("multipart", "form-data"))
                await ReadForm(exchange, multipart, targetType, created, ct).ConfigureAwait(false);
            else
                await ReadCamel(exchange, multipart, targetType, created, ct).ConfigureAwait(false);
        }
        catch
        {
            foreach (var content in created)
                if (content is IAsyncDisposable disposable)
                    await disposable.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task ReadForm(IExchange exchange, Multipart multipart, Type targetType, List<AttachmentContent> created, CancellationToken ct)
    {
        if (!targetType.IsAssignableFrom(typeof(FormBody)) && !targetType.IsAssignableFrom(typeof(Dictionary<string, string>)))
            throw new NotSupportedException(
                $"MIME multipart: multipart/form-data unmarshals to FormBody (or Dictionary<string, string>), not '{targetType.FullName}'.");

        var form = new FormBody();
        var files = new List<Attachment>();
        foreach (var part in multipart)
        {
            var disposition = part.ContentDisposition;
            string? fieldName = null;
            disposition?.Parameters.TryGetValue("name", out fieldName);
            var fileName = SafeFileName(FileNameOf(part));

            if (fileName is null && part is MimePart field)
            {
                if (string.IsNullOrEmpty(fieldName))
                    throw new InvalidDataException("MIME multipart: a form-data part has no name (RFC 7578 requires one).");
                var bytes = await DecodeToBytes(field, ct).ConfigureAwait(false);
                form.Add(fieldName, (field.ContentType.CharsetEncoding ?? _options.DefaultCharset).GetString(bytes));
                continue;
            }

            var content = await DecodeToCache(exchange, part, ct).ConfigureAwait(false);
            created.Add(content);
            files.Add(new Attachment
            {
                Name = fileName ?? fieldName ?? throw new InvalidDataException("MIME multipart: a form-data part has neither a name nor a file name."),
                FileName = fileName,
                ContentType = HeaderValue(part, HeaderId.ContentType) ?? Attachment.DefaultContentType,
                ContentId = TrimContentId(part.ContentId),
                Headers = PartHeaders(part),
                Content = content
            });
        }

        var message = exchange.In;
        message.Body = targetType.IsAssignableFrom(typeof(FormBody)) ? form : form.ToDictionary();
        foreach (var file in files)
            message.Attachments.Add(file);
        // The body is a model now; a boundary left on the message would describe bytes that no longer exist.
        message.ContentType = null;
        message.Headers.Remove("Content-Type");
    }

    private async Task ReadCamel(IExchange exchange, Multipart multipart, Type targetType, List<AttachmentContent> created, CancellationToken ct)
    {
        var message = exchange.In;
        if (multipart.Count == 0)
            throw new InvalidDataException("MIME multipart: the body has no parts.");

        var parts = multipart.ToList();
        var attachments = new List<Attachment>();
        foreach (var part in parts.Skip(1))
        {
            var content = await DecodeToCache(exchange, part, ct).ConfigureAwait(false);
            created.Add(content);
            var fileName = SafeFileName(FileNameOf(part));
            var contentId = TrimContentId(part.ContentId);
            attachments.Add(new Attachment
            {
                Name = fileName ?? contentId ?? $"{Guid.NewGuid():N}@redb",
                FileName = fileName,
                ContentType = HeaderValue(part, HeaderId.ContentType) ?? Attachment.DefaultContentType,
                ContentId = contentId,
                Headers = PartHeaders(part),
                Content = content
            });
        }

        var first = parts[0];
        var firstType = HeaderValue(first, HeaderId.ContentType) ?? Attachment.DefaultContentType;
        if (targetType == typeof(string))
        {
            var bytes = first is MimePart text ? await DecodeToBytes(text, ct).ConfigureAwait(false) : [];
            message.Body = (first.ContentType.CharsetEncoding ?? _options.DefaultCharset).GetString(bytes);
        }
        else if (targetType == typeof(byte[]))
        {
            message.Body = first is MimePart binary ? await DecodeToBytes(binary, ct).ConfigureAwait(false) : [];
        }
        else if (targetType.IsAssignableFrom(typeof(StreamCache)))
        {
            var content = await DecodeToStreamCache(exchange, first, ct).ConfigureAwait(false);
            message.Body = content;
        }
        else
        {
            throw new NotSupportedException(
                $"MIME multipart: the first part of {multipart.ContentType.MimeType} unmarshals to byte[], string or a Stream, not '{targetType.FullName}'.");
        }

        foreach (var attachment in attachments)
            message.Attachments.Add(attachment);
        message.ContentType = firstType;
        message.Headers["Content-Type"] = firstType;
    }

    private async Task<byte[]> DecodeToBytes(MimePart part, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        if (part.Content is not null)
            await part.Content.DecodeToAsync(new LimitedWriteStream(buffer, _options.MaxPartBytes), ct).ConfigureAwait(false);
        return buffer.ToArray();
    }

    private async Task<AttachmentContent> DecodeToCache(IExchange exchange, MimeEntity part, CancellationToken ct) =>
        AttachmentContent.FromStreamCache(await DecodeToStreamCache(exchange, part, ct).ConfigureAwait(false));

    // A part's decoded content, spooled to disk past the engine's stream cache threshold. A nested multipart or a
    // message/rfc822 part is kept as its own bytes, not parsed further.
    private async Task<StreamCache> DecodeToStreamCache(IExchange exchange, MimeEntity part, CancellationToken ct)
    {
        var cache = new StreamCache(exchange.Context.GetStreamCacheOptions());
        try
        {
            var limited = new LimitedWriteStream(cache, _options.MaxPartBytes);
            if (part is MimePart leaf)
            {
                if (leaf.Content is not null)
                    await leaf.Content.DecodeToAsync(limited, ct).ConfigureAwait(false);
            }
            else
            {
                await part.WriteToAsync(FormatOptions.Default, limited, contentOnly: true, ct).ConfigureAwait(false);
            }
            cache.CompleteWriting();
            return cache;
        }
        catch
        {
            await cache.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    // A Windows path sent by a browser or an old client keeps its backslashes raw inside the quotes (RFC 7578 has no
    // escaping there); MimeKit reads a quoted backslash as an escape and would glue the directories to the name. Such a
    // name is taken from the header as written; any other (filename*, RFC 2047) is MimeKit's decoding.
    private static string? FileNameOf(MimeEntity part)
    {
        var disposition = part.ContentDisposition;
        if (disposition is null)
            return null;
        if (part.Headers[HeaderId.ContentDisposition] is { } raw
            && QuotedFileName.Match(raw) is { Success: true } quoted
            && quoted.Groups[1].Value.Contains('\\'))
            return quoted.Groups[1].Value;
        return disposition.FileName;
    }

    private string? SafeFileName(string? fileName)
    {
        if (string.IsNullOrEmpty(fileName))
            return null;
        // The directory a browser or an old client sent goes (Camel's FileUtil.stripPath), on either separator.
        var cut = fileName.LastIndexOfAny(['/', '\\']);
        var bare = cut >= 0 ? fileName[(cut + 1)..] : fileName;
        bare = bare.Replace('\r', '_').Replace('\n', '_').Replace('\t', '_');
        if (bare.Length == 0)
            return null;
        if (bare.Length > _options.MaxFileNameLength)
            throw new InvalidDataException(
                $"MIME multipart: a part has a file name of {bare.Length} characters, more than {_options.MaxFileNameLength} (MaxFileNameLength).");
        return bare;
    }

    private static string? TrimContentId(string? contentId) =>
        string.IsNullOrEmpty(contentId) ? null : contentId.Trim('<', '>', ' ');

    private static string? HeaderValue(MimeEntity part, HeaderId id) =>
        part.Headers[id] is { Length: > 0 } value ? Folding.Replace(value, " ").Trim() : null;

    private static Dictionary<string, string> PartHeaders(MimeEntity part)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in part.Headers)
            headers[header.Field] = Folding.Replace(header.Value ?? string.Empty, " ").Trim();
        return headers;
    }

    private static string DetectBoundary(ReadOnlySpan<byte> body)
    {
        // Without a preamble the first line is the delimiter: "--" and the boundary.
        if (body.Length < 3 || body[0] != (byte)'-' || body[1] != (byte)'-')
            throw new InvalidDataException("MIME multipart: the content type has no boundary and the body does not start with one.");
        var end = body.IndexOfAny((byte)'\r', (byte)'\n');
        var line = end < 0 ? body[2..] : body[2..end];
        var boundary = Encoding.ASCII.GetString(line).TrimEnd();
        if (boundary.Length is 0 or > 70)
            throw new InvalidDataException("MIME multipart: the content type has no boundary and the first line of the body is not one.");
        return boundary;
    }

    // ── Marshal ──

    /// <inheritdoc />
    public async ValueTask MarshalAsync(IExchange exchange, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(exchange);
        var message = exchange.In;
        var attachments = message.Attachments;
        var form = _options.SubType == "form-data";
        if (!form && attachments.Count == 0 && !_options.MultipartWithoutAttachment)
            return;

        var multipart = new Multipart(_options.SubType);
        var opened = new List<Stream>();
        try
        {
            if (form)
                AddFields(multipart, message.Body);
            else
                multipart.Add(await BodyPart(message, opened, ct).ConfigureAwait(false));

            foreach (var attachment in attachments)
                multipart.Add(await AttachmentPart(attachment, form, opened, ct).ConfigureAwait(false));

            var cache = new StreamCache(exchange.Context.GetStreamCacheOptions());
            try
            {
                var format = FormatOptions.Default.Clone();
                format.NewLineFormat = NewLineFormat.Dos;
                // RFC 7578 and every browser: a non-ASCII file name goes as UTF-8 text, not RFC 2231.
                format.International = form;
                await multipart.WriteToAsync(format, cache, contentOnly: true, ct).ConfigureAwait(false);
                cache.CompleteWriting();
            }
            catch
            {
                await cache.DisposeAsync().ConfigureAwait(false);
                throw;
            }

            var contentType = $"multipart/{_options.SubType}; boundary=\"{multipart.Boundary}\"";
            message.Body = cache;
            message.ContentType = contentType;
            message.Headers["Content-Type"] = contentType;
        }
        finally
        {
            foreach (var stream in opened)
                await stream.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static void AddFields(Multipart multipart, object? body)
    {
        IEnumerable<KeyValuePair<string, string>> fields = body switch
        {
            null => [],
            IEnumerable<KeyValuePair<string, string>> pairs => pairs,
            _ => throw new InvalidOperationException(
                $"MIME multipart: multipart/form-data is written from a FormBody (or string pairs) and the attachments, not from '{body.GetType().FullName}'.")
        };
        foreach (var (name, value) in fields)
        {
            // RFC 7578 4.7: no Content-Transfer-Encoding on a form part; the value goes as its UTF-8 bytes.
            var part = new MimePart("text", "plain")
            {
                Content = new MimeContent(new MemoryStream(Encoding.UTF8.GetBytes(value)))
            };
            part.ContentType.Charset = "utf-8";
            SetFormDisposition(part, name, fileName: null);
            multipart.Add(part);
        }
    }

    private static async Task<MimePart> BodyPart(IMessage message, List<Stream> opened, CancellationToken ct)
    {
        Stream content = message.Body switch
        {
            null => new MemoryStream(),
            byte[] bytes => new MemoryStream(bytes, writable: false),
            string text => new MemoryStream(Encoding.UTF8.GetBytes(text), writable: false),
            Stream stream => await Buffered(stream, ct).ConfigureAwait(false),
            _ => throw new InvalidOperationException(
                $"MIME multipart: the first part is written from a byte[], string or Stream body, not '{message.Body.GetType().FullName}'.")
        };
        opened.Add(content);
        var type = MimeContentType.Parse(message.ContentType ?? (message.Body is string ? "text/plain; charset=utf-8" : Attachment.DefaultContentType));
        return new MimePart(type)
        {
            Content = new MimeContent(content),
            ContentTransferEncoding = type.IsMimeType("text", "*") ? ContentEncoding.QuotedPrintable : ContentEncoding.Base64
        };
    }

    // A forward-only body stream is read once into memory: MimeKit may read a part more than once while writing.
    private static async Task<Stream> Buffered(Stream stream, CancellationToken ct)
    {
        if (stream.CanSeek)
        {
            stream.Position = 0;
            return new NonClosingStream(stream);
        }
        var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, ct).ConfigureAwait(false);
        buffer.Position = 0;
        return buffer;
    }

    private static async Task<MimePart> AttachmentPart(Attachment attachment, bool form, List<Stream> opened, CancellationToken ct)
    {
        var stream = await attachment.Content.OpenReadAsync(ct).ConfigureAwait(false);
        opened.Add(stream);
        var fileName = attachment.FileName ?? attachment.Name;
        var part = new MimePart(MimeContentType.Parse(attachment.ContentType)) { Content = new MimeContent(stream) };
        if (form)
        {
            // A form part carries its raw bytes (RFC 7578 4.7): no transfer encoding.
            SetFormDisposition(part, FieldName(attachment), fileName);
        }
        else
        {
            // A mail-style part is base64, safe for any transport.
            part.ContentDisposition = new ContentDisposition(ContentDisposition.Attachment) { FileName = fileName };
            part.ContentTransferEncoding = ContentEncoding.Base64;
        }
        if (attachment.ContentId is { } id)
            part.ContentId = id;
        return part;
    }

    // As browsers write it (the HTML form submission algorithm): name first, then filename, both quoted, UTF-8 as is,
    // a quote as %22 and a line break as %0D / %0A. Written as the raw header: MimeKit would re-encode the parameters.
    private static void SetFormDisposition(MimePart part, string name, string? fileName)
    {
        static string Quote(string value) =>
            value.Replace("\"", "%22").Replace("\r", "%0D").Replace("\n", "%0A");

        var value = $"form-data; name=\"{Quote(name)}\"" + (fileName is null ? "" : $"; filename=\"{Quote(fileName)}\"");
        var header = new Header(HeaderId.ContentDisposition, "form-data");
        header.SetRawValue(Encoding.UTF8.GetBytes($" {value}\r\n"));
        part.Headers.Replace(header);
    }

    // The form field an attachment read from a form belongs to is in its Content-Disposition; any other goes under its name.
    private static string FieldName(Attachment attachment)
    {
        if (attachment.Headers.TryGetValue("Content-Disposition", out var raw)
            && ContentDisposition.TryParse(raw, out var disposition)
            && disposition.Parameters.TryGetValue("name", out string? name)
            && !string.IsNullOrEmpty(name))
            return name;
        return attachment.Name;
    }

    /// <summary>Reads at most a limit from a stream and fails past it, so a huge body is refused while it is read.</summary>
    private sealed class LimitedReadStream(Stream inner, long limit) : Stream
    {
        private long _read;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _read; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
            Count(await inner.ReadAsync(buffer, ct).ConfigureAwait(false));

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

        private int Count(int read)
        {
            _read += read;
            if (_read > limit)
                throw new InvalidDataException($"MIME multipart: the body is larger than {limit} bytes (MaxTotalBytes).");
            return read;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>Writes through to a stream and fails past a limit: one decoded part may not exceed MaxPartBytes.</summary>
    private sealed class LimitedWriteStream(Stream inner, long limit) : Stream
    {
        private long _written;

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _written;
        public override long Position { get => _written; set => throw new NotSupportedException(); }

        public override void Write(byte[] buffer, int offset, int count)
        {
            Count(count);
            inner.Write(buffer, offset, count);
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            Count(buffer.Length);
            await inner.WriteAsync(buffer, ct).ConfigureAwait(false);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            WriteAsync(buffer.AsMemory(offset, count), ct).AsTask();

        private void Count(int count)
        {
            _written += count;
            if (_written > limit)
                throw new InvalidDataException($"MIME multipart: a part is larger than {limit} bytes (MaxPartBytes).");
        }

        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    /// <summary>The message's own body stream inside a part: writing the multipart must not close it.</summary>
    private sealed class NonClosingStream(Stream inner) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => inner.ReadAsync(buffer, ct);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => inner.ReadAsync(buffer, offset, count, ct);
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { }
        public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

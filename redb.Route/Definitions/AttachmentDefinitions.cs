using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Expressions;
using redb.Route.Processors;

namespace redb.Route.Definitions;

/// <summary>
/// Where an attachment step takes its content from: an expression, the body, or a file. The body and file forms are
/// the common cases spelled out so a route needs no expression for them.
/// </summary>
public enum AttachmentSource
{
    /// <summary>The value of an expression.</summary>
    Expression,

    /// <summary>The message body; the body itself stays.</summary>
    Body,

    /// <summary>A file, read from disk on every send.</summary>
    File
}

/// <summary>
/// Leaf definition that adds an attachment to the In message. The name and the content type are literal; the file
/// name and the file path are literal or an expression, chosen by the overload (never guessed from the text).
/// </summary>
/// <remarks>
/// Content of an expression or the body: <c>byte[]</c> as is, <c>string</c> as UTF-8, a completed
/// <see cref="StreamCache"/> shared (re-readable), another <see cref="Stream"/> read into memory, an
/// <see cref="AttachmentContent"/> as is. A file is not read here: the attachment opens it on every read.
/// </remarks>
public sealed class AddAttachmentDefinition : ProcessorDefinition
{
    /// <summary>The content source.</summary>
    public AttachmentSource Source { get; }

    /// <summary>The attachment name; for a file it may be <c>null</c>, the file name then.</summary>
    public string? Name { get; }

    /// <summary>Content (<see cref="AttachmentSource.Expression"/>) or file path (<see cref="AttachmentSource.File"/>).</summary>
    public IExpression? Content { get; }

    /// <summary>File name, or <c>null</c> (a file attachment uses the file's name).</summary>
    public IExpression? FileName { get; }

    /// <summary>Content type, or <c>null</c> for the default of the source.</summary>
    public string? ContentType { get; }

    private AddAttachmentDefinition(AttachmentSource source, string? name, IExpression? content, IExpression? fileName, string? contentType)
    {
        if (name is not null)
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (contentType is not null)
            ArgumentException.ThrowIfNullOrWhiteSpace(contentType);
        Source = source;
        Name = name;
        Content = content;
        FileName = fileName;
        ContentType = contentType;
    }

    /// <summary>An attachment with the value of <paramref name="content"/>.</summary>
    public static AddAttachmentDefinition FromExpression(string name, IExpression content, IExpression? fileName = null, string? contentType = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(content);
        return new(AttachmentSource.Expression, name, content, fileName, contentType);
    }

    /// <summary>An attachment with the message body as content.</summary>
    public static AddAttachmentDefinition FromBody(string name, IExpression? fileName = null, string? contentType = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return new(AttachmentSource.Body, name, null, fileName, contentType);
    }

    /// <summary>An attachment with the content of the file at <paramref name="path"/>.</summary>
    public static AddAttachmentDefinition FromFile(IExpression path, string? name = null, string? contentType = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        return new(AttachmentSource.File, name, path, null, contentType);
    }

    /// <summary>A literal as an expression: what the string overloads of the DSL pass.</summary>
    internal static IExpression? Literal(string? text) => text is null ? null : new ConstantExpression(text);

    /// <inheritdoc />
    public override IProcessor CreateProcessor(IRouteContext context)
    {
        var name = Name;
        var fileName = FileName;
        var contentType = ContentType;
        var source = Source;
        var content = Content;

        return new DelegateProcessor(async (exchange, ct) =>
        {
            var message = exchange.In;
            AttachmentContent attachmentContent;
            string defaultType;
            var resolvedName = name;
            var resolvedFileName = fileName is null ? null : Evaluate(fileName, exchange, "file name");

            switch (source)
            {
                case AttachmentSource.File:
                {
                    var path = Evaluate(content!, exchange, "file path");
                    attachmentContent = AttachmentContent.FromFile(path);
                    var bare = Path.GetFileName(path);
                    resolvedName ??= bare;
                    resolvedFileName ??= bare;
                    defaultType = Attachment.DefaultContentType;
                    break;
                }
                case AttachmentSource.Body:
                    (attachmentContent, defaultType) = await ToContent(message.Body, "the body", ct).ConfigureAwait(false);
                    defaultType = message.ContentType ?? defaultType;
                    break;
                default:
                    (attachmentContent, defaultType) = await ToContent(content!.Evaluate<object?>(exchange), $"attachment '{resolvedName}'", ct)
                        .ConfigureAwait(false);
                    break;
            }

            message.Attachments.Add(new Attachment
            {
                Name = resolvedName!,
                FileName = resolvedFileName,
                ContentType = contentType ?? defaultType,
                Content = attachmentContent
            });
        });
    }

    private static async Task<(AttachmentContent Content, string DefaultType)> ToContent(object? value, string what, CancellationToken ct)
    {
        switch (value)
        {
            case null:
                throw new InvalidOperationException($"Cannot attach {what}: it is null.");
            case AttachmentContent ready:
                return (ready, Attachment.DefaultContentType);
            case byte[] bytes:
                return (AttachmentContent.FromBytes(bytes), Attachment.DefaultContentType);
            case string text:
                return (AttachmentContent.FromBytes(System.Text.Encoding.UTF8.GetBytes(text)), "text/plain; charset=utf-8");
            case StreamCache { CanRead: true } cache:
                return (AttachmentContent.FromStreamCache(cache), Attachment.DefaultContentType);
            case Stream stream:
            {
                using var buffer = new MemoryStream();
                await stream.CopyToAsync(buffer, ct).ConfigureAwait(false);
                return (AttachmentContent.FromBytes(buffer.ToArray()), Attachment.DefaultContentType);
            }
            default:
                throw new InvalidOperationException(
                    $"Cannot attach {what} of type '{value.GetType().FullName}': use byte[], string, Stream or AttachmentContent.");
        }
    }

    private static string Evaluate(IExpression expression, IExchange exchange, string field) =>
        expression.Evaluate<object?>(exchange)?.ToString() is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"The attachment {field} evaluated to nothing.");
}

/// <summary>Leaf definition that removes the attachments with a name from the In message.</summary>
public sealed class RemoveAttachmentDefinition : ProcessorDefinition
{
    /// <summary>The name of the attachments to remove.</summary>
    public string Name { get; }

    /// <summary>Creates the definition.</summary>
    public RemoveAttachmentDefinition(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
    }

    /// <inheritdoc />
    public override IProcessor CreateProcessor(IRouteContext context)
        => new DelegateProcessor(exchange => exchange.In.Attachments.Remove(Name));
}

/// <summary>Leaf definition that removes every attachment of the In message.</summary>
public sealed class RemoveAttachmentsDefinition : ProcessorDefinition
{
    /// <inheritdoc />
    public override IProcessor CreateProcessor(IRouteContext context)
        => new DelegateProcessor(exchange => exchange.In.Attachments.Clear());
}

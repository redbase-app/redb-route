namespace redb.Route.Abstractions;

/// <summary>
/// A data format that works on the whole exchange rather than on bytes alone, as Apache Camel's <c>DataFormat</c>
/// receives the exchange: it reads the content type of the In message with its parameters (a <c>boundary</c>, a
/// <c>charset</c>), reads the body as it came (<c>byte[]</c>, <c>string</c> or a <see cref="Stream"/>, without buffering
/// by the step), writes the body, the content type and the attachments, and takes engine settings from
/// <see cref="IExchange.Context"/> (the stream cache a large part spools to). <c>Marshal</c>, <c>Unmarshal</c> and
/// <c>ConvertBody</c> call these methods when the resolved format implements this interface, and the byte methods of
/// <see cref="IMessageSerializer"/> otherwise.
/// </summary>
/// <remarks>
/// Registration, lookup by content type and the XML <c>dataFormat</c> element are those of
/// <see cref="IMessageSerializer"/>. A format sets <see cref="IMessage.ContentType"/> and the <c>Content-Type</c> header
/// itself; the step does not overwrite them. A format decides itself what to do with a body that is not its own (MIME
/// multipart leaves it unchanged, as Camel's <c>mimeMultipart</c> does).
/// </remarks>
public interface IMessageDataFormat : IMessageSerializer
{
    /// <summary>Turns the wire form in <c>exchange.In</c> into <paramref name="targetType"/>, in place.</summary>
    ValueTask UnmarshalAsync(IExchange exchange, Type targetType, CancellationToken ct = default);

    /// <summary>Turns the body of <c>exchange.In</c> (and its attachments, when the format carries them) into the wire form, in place.</summary>
    ValueTask MarshalAsync(IExchange exchange, CancellationToken ct = default);
}

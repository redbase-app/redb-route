using System.Collections;
using System.Collections.Concurrent;

namespace redb.Route.Core;

/// <summary>
/// The text a producer sends for a body: text as it is, bytes and a stream as UTF-8 text (as Camel converts them), any
/// other body its own <c>ToString()</c>. A body without a text form of its own, whose <c>ToString()</c> is only its type name (a list,
/// an object that does not override it), is refused instead of sending that name, the way Apache Camel's type
/// conversion fails for a body it cannot convert.
/// </summary>
public static class BodyText
{
    private static readonly ConcurrentDictionary<Type, bool> OwnToString = new();

    /// <summary>Whether <paramref name="body"/> writes itself as text (overrides <c>ToString()</c>).</summary>
    public static bool HasOwnText(object body)
    {
        ArgumentNullException.ThrowIfNull(body);
        return OwnToString.GetOrAdd(body.GetType(),
            static type => type.GetMethod(nameof(ToString), Type.EmptyTypes)?.DeclaringType != typeof(object));
    }

    /// <summary>
    /// The body's own text, or a refusal naming the endpoint (sanitized by the caller) and the body's type when its text
    /// would be the type name.
    /// </summary>
    public static string Of(object body, string? endpoint = null)
    {
        ArgumentNullException.ThrowIfNull(body);
        switch (body)
        {
            case string text:
                return text;
            case byte[] bytes:
                return System.Text.Encoding.UTF8.GetString(bytes);
            case ReadOnlyMemory<byte> memory:
                return System.Text.Encoding.UTF8.GetString(memory.Span);
            case Stream stream:
                // As Camel's InputStream-to-String conversion: the stream's content as UTF-8 text, read to its end.
                if (stream.CanSeek)
                    stream.Position = 0;
                using (var reader = new StreamReader(stream, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true,
                           bufferSize: 4096, leaveOpen: true))
                    return reader.ReadToEnd();
        }
        if (HasOwnText(body))
            return body.ToString() ?? string.Empty;

        var where = endpoint is null ? "This endpoint" : $"Endpoint '{endpoint}'";
        throw body is IEnumerable
            ? new InvalidOperationException(
                $"{where} cannot send a body of type {body.GetType().Name}: a list has no wire form here, and its text " +
                "would be the type name. Serialize the list (to JSON, for example), or Split it.")
            : new InvalidOperationException(
                $"{where} cannot send a body of type {body.GetType().Name}: it has no text form of its own, and its text " +
                "would be the type name. Convert it before the send (Marshal, ConvertBody, a Process step).");
    }
}

using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace redb.Route.Expressions.Tokenizers;

/// <summary>
/// Splits a JSON array body into individual elements.
/// Uses <see cref="JsonDocument.ParseAsync"/> with pooled buffers.
/// </summary>
internal static class JsonArrayTokenizer
{
    /// <summary>Tokenizes a JSON array body into individual raw-text elements.</summary>
    /// <param name="body">Exchange body (Stream, string, or byte[]).</param>
    /// <param name="ct">Cancellation token.</param>
    internal static async IAsyncEnumerable<object?> Tokenize(
        object? body,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var stream = body switch
        {
            Stream s => s,
            byte[] b => new MemoryStream(b, writable: false),
            string str => new MemoryStream(Encoding.UTF8.GetBytes(str)),
            _ => throw new InvalidOperationException(
                $"SplitJsonArray: unsupported body type {body?.GetType().Name ?? "null"}")
        };

        var ownsStream = body is not Stream;

        try
        {
            await foreach (var element in ReadArrayElementsAsync(stream, ct).ConfigureAwait(false))
            {
                yield return element;
            }
        }
        finally
        {
            if (ownsStream) await stream.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async IAsyncEnumerable<string> ReadArrayElementsAsync(
        Stream stream, [EnumeratorCancellation] CancellationToken ct)
    {
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct)
            .ConfigureAwait(false);

        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("SplitJsonArray: body is not a JSON array.");

        foreach (var element in doc.RootElement.EnumerateArray())
        {
            ct.ThrowIfCancellationRequested();
            yield return element.GetRawText();
        }
    }
}

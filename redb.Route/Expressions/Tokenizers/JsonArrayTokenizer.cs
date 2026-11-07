using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace redb.Route.Expressions.Tokenizers;

/// <summary>
/// Splits a JSON array body into individual elements, each as its JSON text.
/// JSON text (<see cref="Stream"/>, <see cref="string"/>, <c>byte[]</c>) is read with
/// <see cref="JsonDocument.ParseAsync"/>; any other body is taken as <c>jpath</c> takes it (a System.Text.Json tree,
/// an array such as the <c>object[]</c> a <c>jpath</c> returns, or a POCO).
/// </summary>
internal static class JsonArrayTokenizer
{
    // Element text as close to the source as the tree allows: non-ASCII stays as written, not \u-escaped.
    private static readonly JsonSerializerOptions ElementText = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // C# member names, as jpath serializes a POCO body.
    private static readonly JsonSerializerOptions PocoOptions = new();

    /// <summary>Tokenizes a JSON array body into individual raw-text elements.</summary>
    /// <param name="body">Exchange body: JSON text, a JSON tree, a CLR array or collection, or a POCO.</param>
    /// <param name="ct">Cancellation token.</param>
    internal static async IAsyncEnumerable<object?> Tokenize(
        object? body,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (body is not (Stream or byte[] or string))
        {
            if (body is null)
                throw new InvalidOperationException("SplitJsonArray: the body is null.");

            if (JsonPathValues.ToNode(body, default, PocoOptions) is not JsonArray array)
                throw new InvalidOperationException("SplitJsonArray: body is not a JSON array.");

            foreach (var element in array)
            {
                ct.ThrowIfCancellationRequested();
                yield return element is null ? "null" : element.ToJsonString(ElementText);
            }
            yield break;
        }

        var stream = body switch
        {
            Stream s => s,
            byte[] b => new MemoryStream(b, writable: false),
            _ => new MemoryStream(Encoding.UTF8.GetBytes((string)body)),
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

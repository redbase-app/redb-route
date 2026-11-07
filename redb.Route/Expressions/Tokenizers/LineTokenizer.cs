using System.Runtime.CompilerServices;
using System.Text;

namespace redb.Route.Expressions.Tokenizers;

/// <summary>
/// Splits body content into lines using a configurable separator.
/// Supports <see cref="Stream"/>, <see cref="string"/>, and <c>byte[]</c> bodies.
/// </summary>
internal static class LineTokenizer
{
    /// <summary>Tokenizes the body into individual lines.</summary>
    /// <param name="body">Exchange body (Stream, string, or byte[]).</param>
    /// <param name="separator">Line separator string.</param>
    /// <param name="skipEmpty">Whether to skip empty/whitespace lines.</param>
    /// <param name="ct">Cancellation token.</param>
    internal static async IAsyncEnumerable<object?> Tokenize(
        object? body, string separator, bool skipEmpty,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        switch (body)
        {
            case Stream stream:
                using (var reader = new StreamReader(stream, leaveOpen: true))
                {
                    while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
                    {
                        if (skipEmpty && string.IsNullOrWhiteSpace(line)) continue;
                        yield return line;
                    }
                }
                break;

            case string str:
                foreach (var line in str.Split(separator))
                {
                    ct.ThrowIfCancellationRequested();
                    if (skipEmpty && string.IsNullOrWhiteSpace(line)) continue;
                    yield return line;
                }
                break;

            case byte[] bytes:
                var text = Encoding.UTF8.GetString(bytes);
                foreach (var line in text.Split(separator))
                {
                    ct.ThrowIfCancellationRequested();
                    if (skipEmpty && string.IsNullOrWhiteSpace(line)) continue;
                    yield return line;
                }
                break;
        }
    }
}

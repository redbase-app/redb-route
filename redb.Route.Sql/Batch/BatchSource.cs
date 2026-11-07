using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml;

namespace redb.Route.Sql.Batch;

/// <summary>
/// Decides whether a message body is a batch source and opens a reader over its items. A source is a list, any other
/// sequence (a <c>HashSet</c>, a LINQ query, a <c>yield</c> method), an <c>IAsyncEnumerable</c> of a reference type (a
/// streamed query result), or a JSON array (<see cref="JsonElement"/>, a <see cref="JsonDocument"/> whose root is an array,
/// <see cref="JsonArray"/>). A value that merely happens to be enumerable is one value, not a batch: a string or any other
/// sequence of characters, a <c>byte[]</c> or any other sequence of bytes, a dictionary or JSON object (a map of named
/// values), and an XML document or node — XML carries no batch shape of its own, as in Apache Camel.
/// </summary>
internal static class BatchSource
{
    /// <summary>Opens a reader over the items of <paramref name="body"/> when it is a batch source.</summary>
    internal static bool TryOpen(object? body, CancellationToken ct, [NotNullWhen(true)] out BatchItemReader? reader)
    {
        reader = body switch
        {
            null or string or byte[] => null,
            IEnumerable<byte> or IEnumerable<char> => null,
            JsonElement { ValueKind: JsonValueKind.Array } array => BatchItemReader.Over(array.EnumerateArray().Cast<object?>(), ct),
            JsonDocument { RootElement.ValueKind: JsonValueKind.Array } document =>
                BatchItemReader.Over(document.RootElement.EnumerateArray().Cast<object?>(), ct),
            JsonElement or JsonDocument => null,
            IDictionary or XmlNode => null,
            _ when IsGenericMap(body.GetType()) => null,
            IAsyncEnumerable<object?> stream => BatchItemReader.Over(stream, ct),
            IEnumerable sequence => BatchItemReader.Over(sequence, ct),
            _ => null,
        };
        return reader is not null;
    }

    /// <summary>A generic dictionary — <see cref="JsonObject"/> included — is a map of named values, not a sequence of items.</summary>
    private static bool IsGenericMap(Type type) =>
        type.GetInterfaces().Any(i => i.IsGenericType
            && (i.GetGenericTypeDefinition() == typeof(IDictionary<,>) || i.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>)));
}

using System.Text.Json;

namespace redb.Route.Llm.Tools;

/// <summary>
/// Internal JSON helpers shared by the LLM-tool DSL extensions
/// (<see cref="HttpFetchDsl"/>, <see cref="JsonPathDsl"/>, <see cref="XPathDsl"/>,
/// <see cref="RegexExtractDsl"/>, <see cref="MathEvalDsl"/>, <see cref="TavilyWebSearchDsl"/>).
/// </summary>
internal static class LlmToolJson
{
    /// <summary>
    /// Parses <paramref name="body"/> as a JSON object. Accepts either a <see cref="string"/>
    /// or any other type whose <see cref="object.ToString"/> renders the JSON document.
    /// Throws <see cref="ArgumentException"/> on null / non-object / malformed input.
    /// </summary>
    /// <param name="body">The raw exchange body (typically <c>exchange.In.Body</c>).</param>
    /// <param name="toolName">Tool name used to build a helpful error message.</param>
    /// <returns>A disposable <see cref="JsonDocument"/> whose root is an object.</returns>
    public static JsonDocument ParseObject(object? body, string toolName)
    {
        if (body is null)
            throw new ArgumentException($"{toolName} input is empty — expected a JSON object.");

        var raw = body as string ?? body.ToString() ?? string.Empty;
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(raw);
        }
        catch (JsonException ex)
        {
            throw new ArgumentException($"{toolName} input is not valid JSON.", ex);
        }

        if (doc.RootElement.ValueKind != JsonValueKind.Object)
        {
            doc.Dispose();
            throw new ArgumentException($"{toolName} input must be a JSON object.");
        }

        return doc;
    }

    /// <summary>Reads a required string property from a JSON object root.</summary>
    public static string RequiredString(JsonElement root, string name, string toolName)
    {
        if (!root.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.String)
            throw new ArgumentException($"{toolName} input must include '{name}' as a string.");
        return v.GetString() ?? string.Empty;
    }

    /// <summary>Reads an optional string property, returning <paramref name="fallback"/> when missing.</summary>
    public static string? OptionalString(JsonElement root, string name, string? fallback = null) =>
        root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : fallback;

    /// <summary>Reads an optional bool property, returning <paramref name="fallback"/> when missing or not a bool.</summary>
    public static bool OptionalBool(JsonElement root, string name, bool fallback = false) =>
        root.TryGetProperty(name, out var v)
            ? v.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => fallback
            }
            : fallback;

    /// <summary>Reads an optional integer property, returning <paramref name="fallback"/> when missing or not numeric.</summary>
    public static int? OptionalInt(JsonElement root, string name, int? fallback = null) =>
        root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i)
            ? i
            : fallback;
}

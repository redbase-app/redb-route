using System.Text.Json;
using System.Text.Json.Nodes;

namespace redb.Route.Llm.Providers;

/// <summary>
/// Reading the <c>data:</c> frames of a provider's SSE stream. A frame that cannot be read is lost text or a lost tool
/// call: skipping it would hand on a shorter answer as a whole one, so it fails, and the message shows the frame.
/// </summary>
internal static class LlmStreamFrames
{
    private const int MaxShown = 500;

    /// <summary>The frame as a JSON object; anything else fails with the frame in the message.</summary>
    internal static JsonObject Read(string providerId, string payload)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(payload);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"{providerId}: a stream frame is not JSON: {Shown(payload)}", ex);
        }

        return node as JsonObject
               ?? throw new InvalidOperationException($"{providerId}: a stream frame is not a JSON object: {Shown(payload)}");
    }

    /// <summary>The string value of <paramref name="node"/>, or null when it is absent or not a string.</summary>
    internal static string? Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    /// <summary><paramref name="text"/> cut to a length a log line can carry.</summary>
    internal static string Shown(string text) =>
        text.Length <= MaxShown ? text : string.Concat(text.AsSpan(0, MaxShown), "…");
}

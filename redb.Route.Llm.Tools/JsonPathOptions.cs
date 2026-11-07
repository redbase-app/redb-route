namespace redb.Route.Llm.Tools;

/// <summary>
/// Configuration for <see cref="JsonPathTool"/>.
/// </summary>
public sealed class JsonPathOptions
{
    /// <summary>
    /// Endpoint URI the tool is mounted on. Default <c>"direct:llm.json_path"</c>.
    /// </summary>
    public string EndpointUri { get; init; } = "direct:llm.json_path";

    /// <summary>
    /// Tool name exposed to the model. Default <c>"json_path"</c>.
    /// </summary>
    public string ToolName { get; init; } = "json_path";

    /// <summary>
    /// Maximum length of the inbound <c>json</c> string in characters. Anything
    /// larger is rejected with an <see cref="ArgumentException"/> before parsing
    /// — guards against the model handing the tool an entire response body.
    /// Default 1 MiB.
    /// </summary>
    public int MaxJsonChars { get; init; } = 1_048_576;
}

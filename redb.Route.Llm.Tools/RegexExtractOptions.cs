namespace redb.Route.Llm.Tools;

/// <summary>
/// Configuration for <see cref="RegexExtractTool"/>.
/// </summary>
public sealed class RegexExtractOptions
{
    /// <summary>Endpoint URI the tool is mounted on. Default <c>"direct:llm.regex_extract"</c>.</summary>
    public string EndpointUri { get; init; } = "direct:llm.regex_extract";

    /// <summary>Tool name exposed to the model. Default <c>"regex_extract"</c>.</summary>
    public string ToolName { get; init; } = "regex_extract";

    /// <summary>
    /// Maximum length of the inbound <c>text</c> in characters. Default 1 MiB.
    /// </summary>
    public int MaxTextChars { get; init; } = 1_048_576;

    /// <summary>
    /// Per-pattern execution timeout. Default 1 second. Guards against catastrophic
    /// backtracking when the model produces a pathological pattern.
    /// </summary>
    public TimeSpan MatchTimeout { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Maximum number of matches returned in array mode. Default 64.
    /// </summary>
    public int MaxMatches { get; init; } = 64;
}

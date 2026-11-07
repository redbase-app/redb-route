namespace redb.Route.Llm.Tools;

/// <summary>
/// Configuration for <see cref="XPathTool"/>.
/// </summary>
public sealed class XPathOptions
{
    /// <summary>Endpoint URI the tool is mounted on. Default <c>"direct:llm.xpath"</c>.</summary>
    public string EndpointUri { get; init; } = "direct:llm.xpath";

    /// <summary>Tool name exposed to the model. Default <c>"xpath"</c>.</summary>
    public string ToolName { get; init; } = "xpath";

    /// <summary>
    /// Maximum length of the inbound <c>xml</c> string in characters. Default 1 MiB.
    /// </summary>
    public int MaxXmlChars { get; init; } = 1_048_576;
}

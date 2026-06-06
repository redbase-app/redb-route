namespace redb.Route.Llm.Tools;

/// <summary>
/// Configuration for <see cref="TavilyWebSearchTool"/>.
/// </summary>
public sealed class TavilyWebSearchOptions
{
    /// <summary>Endpoint URI the tool is mounted on. Default <c>"direct:llm.web_search"</c>.</summary>
    public string EndpointUri { get; init; } = "direct:llm.web_search";

    /// <summary>Tool name exposed to the model. Default <c>"web_search"</c>.</summary>
    public string ToolName { get; init; } = "web_search";

    /// <summary>
    /// Tavily API key. Required. Get one at https://tavily.com/.
    /// </summary>
    public string ApiKey { get; init; } = string.Empty;

    /// <summary>
    /// Tavily search endpoint. Override only for testing/proxying.
    /// Default <c>https://api.tavily.com/search</c>.
    /// </summary>
    public string Endpoint { get; init; } = "https://api.tavily.com/search";

    /// <summary>
    /// Maximum results to request from Tavily. Default 5. Tavily caps at 20.
    /// </summary>
    public int MaxResults { get; init; } = 5;

    /// <summary>
    /// <c>"basic"</c> (faster, cheaper) or <c>"advanced"</c> (deeper crawl).
    /// Default <c>"basic"</c>.
    /// </summary>
    public string SearchDepth { get; init; } = "basic";

    /// <summary>
    /// When true, ask Tavily to include a short answer summary in the response.
    /// Default true.
    /// </summary>
    public bool IncludeAnswer { get; init; } = true;

    /// <summary>
    /// HTTP request timeout. Default 20 seconds.
    /// </summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(20);
}

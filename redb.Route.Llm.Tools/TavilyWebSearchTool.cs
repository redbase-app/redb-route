using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Llm.Abstractions.Tools;
using redb.Route.Llm.Extensions;

namespace redb.Route.Llm.Tools;

/// <summary>
/// Standalone <c>RouteBuilder</c> wrapper that mounts <see cref="TavilyWebSearchDsl.TavilyWebSearch"/>
/// behind a <see cref="TavilyWebSearchOptions.EndpointUri"/> tool route. Prefer
/// the DSL extension <c>.TavilyWebSearch(opts)</c> on any existing route —
/// this shim is only useful when you want a self-contained <c>RouteBuilder</c>
/// to hand to <c>context.AddRoutes(...)</c>.
/// </summary>
public sealed class TavilyWebSearchTool : RouteBuilder
{
    private const string InputSchema = """
        {
          "type": "object",
          "properties": {
            "query":       { "type": "string", "description": "Web search query." },
            "max_results": { "type": "integer", "description": "Override default max results (1-20)." }
          },
          "required": ["query"],
          "additionalProperties": false
        }
        """;

    private readonly TavilyWebSearchOptions _options;

    /// <summary>Creates the tool with the given options. <see cref="TavilyWebSearchOptions.ApiKey"/> is required.</summary>
    public TavilyWebSearchTool(TavilyWebSearchOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        if (string.IsNullOrWhiteSpace(_options.ApiKey))
            throw new ArgumentException("TavilyWebSearchOptions.ApiKey is required.", nameof(options));
    }

    /// <inheritdoc />
    protected override void Configure() =>
        From(_options.EndpointUri)
            .AsLlmTool(_options.ToolName)
                .Description("Searches the web via Tavily. Returns a JSON object with an optional 'answer' " +
                             "summary and a 'results' array of {title, url, content}.")
                .Input(InputSchema)
                .SideEffect(ToolSideEffect.External)
                .Cost(ToolCostClass.Expensive)
            .Then()
            .TavilyWebSearch(_options);
}

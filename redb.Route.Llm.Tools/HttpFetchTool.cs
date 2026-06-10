using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Llm.Abstractions.Tools;
using redb.Route.Llm.Extensions;

namespace redb.Route.Llm.Tools;

/// <summary>
/// Standalone <c>RouteBuilder</c> wrapper that mounts <see cref="HttpFetchDsl.HttpFetch"/>
/// behind a <see cref="HttpFetchOptions.EndpointUri"/> tool route. Prefer the
/// DSL extension <c>.HttpFetch(opts)</c> on any existing route — this shim is
/// only useful when you want a self-contained <c>RouteBuilder</c> to hand to
/// <c>context.AddRoutes(...)</c>.
/// <para>
/// Input schema: <c>{"url":"https://..."}</c>. Response body is returned as
/// UTF-8 text in <c>exchange.Out.Body</c>; bytes past
/// <see cref="HttpFetchOptions.MaxBytes"/> are truncated.
/// </para>
/// </summary>
public sealed class HttpFetchTool : RouteBuilder
{
    private const string InputSchema = """
        {
          "type": "object",
          "properties": {
            "url": { "type": "string", "description": "Absolute http(s) URL to fetch." }
          },
          "required": ["url"],
          "additionalProperties": false
        }
        """;

    private readonly HttpFetchOptions _options;

    /// <summary>Creates the tool with the given options.</summary>
    public HttpFetchTool(HttpFetchOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <inheritdoc />
    protected override void Configure() =>
        From(_options.EndpointUri)
            .AsLlmTool(_options.ToolName)
                .Description("Fetches a URL via HTTP GET and returns the response body as UTF-8 text.")
                .Input(InputSchema)
                .SideEffect(ToolSideEffect.External)
                .Cost(ToolCostClass.Moderate)
            .Then()
            .HttpFetch(_options);
}

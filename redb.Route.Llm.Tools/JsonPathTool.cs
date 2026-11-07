using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Llm.Abstractions.Tools;
using redb.Route.Llm.Extensions;

namespace redb.Route.Llm.Tools;

/// <summary>
/// Standalone <c>RouteBuilder</c> wrapper that mounts <see cref="JsonPathDsl.JsonPath"/>
/// behind a <see cref="JsonPathOptions.EndpointUri"/> tool route. Prefer the
/// DSL extension <c>.JsonPath(opts)</c> on any existing route — this shim is
/// only useful when you want a self-contained <c>RouteBuilder</c> to hand to
/// <c>context.AddRoutes(...)</c>.
/// </summary>
public sealed class JsonPathTool : RouteBuilder
{
    private const string InputSchema = """
        {
          "type": "object",
          "properties": {
            "json": { "type": "string", "description": "JSON document to query." },
            "path": { "type": "string", "description": "JsonPath expression (full Newtonsoft dialect)." }
          },
          "required": ["json", "path"],
          "additionalProperties": false
        }
        """;

    private readonly JsonPathOptions _options;

    /// <summary>Creates the tool with default options.</summary>
    public JsonPathTool() : this(new JsonPathOptions()) { }

    /// <summary>Creates the tool with the given options.</summary>
    public JsonPathTool(JsonPathOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <inheritdoc />
    protected override void Configure() =>
        From(_options.EndpointUri)
            .AsLlmTool(_options.ToolName)
                .Description("Extracts a value from a JSON document via JsonPath (Newtonsoft dialect: " +
                             "recursive '..', wildcards '[*]', filters '[?...]', slices). Returns the matched " +
                             "value as JSON, or null when nothing matches.")
                .Input(InputSchema)
                .SideEffect(ToolSideEffect.ReadOnly)
                .Cost(ToolCostClass.Cheap)
            .Then()
            .JsonPath(_options);
}

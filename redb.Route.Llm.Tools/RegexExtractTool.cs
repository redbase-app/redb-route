using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Llm.Abstractions.Tools;
using redb.Route.Llm.Extensions;

namespace redb.Route.Llm.Tools;

/// <summary>
/// Standalone <c>RouteBuilder</c> wrapper that mounts <see cref="RegexExtractDsl.RegexExtract"/>
/// behind a <see cref="RegexExtractOptions.EndpointUri"/> tool route. Prefer
/// the DSL extension <c>.RegexExtract(opts)</c> on any existing route — this
/// shim is only useful when you want a self-contained <c>RouteBuilder</c> to
/// hand to <c>context.AddRoutes(...)</c>.
/// </summary>
public sealed class RegexExtractTool : RouteBuilder
{
    private const string InputSchema = """
        {
          "type": "object",
          "properties": {
            "text":    { "type": "string", "description": "Text to scan." },
            "pattern": { "type": "string", "description": ".NET regular expression." },
            "group":   { "type": "string", "description": "Capture group name or number. Omit / '0' for the whole match." },
            "all":     { "type": "boolean", "description": "Return every match as an array. Default false." }
          },
          "required": ["text", "pattern"],
          "additionalProperties": false
        }
        """;

    private readonly RegexExtractOptions _options;

    /// <summary>Creates the tool with default options.</summary>
    public RegexExtractTool() : this(new RegexExtractOptions()) { }

    /// <summary>Creates the tool with the given options.</summary>
    public RegexExtractTool(RegexExtractOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <inheritdoc />
    protected override void Configure() =>
        From(_options.EndpointUri)
            .AsLlmTool(_options.ToolName)
                .Description("Extracts text by a .NET regex. Returns the matched group as JSON " +
                             "(string, array of strings, or null when nothing matches).")
                .Input(InputSchema)
                .SideEffect(ToolSideEffect.ReadOnly)
                .Cost(ToolCostClass.Cheap)
            .Then()
            .RegexExtract(_options);
}

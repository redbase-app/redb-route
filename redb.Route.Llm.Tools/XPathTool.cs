using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Llm.Abstractions.Tools;
using redb.Route.Llm.Extensions;

namespace redb.Route.Llm.Tools;

/// <summary>
/// Standalone <c>RouteBuilder</c> wrapper that mounts <see cref="XPathDsl.XPath"/>
/// behind a <see cref="XPathOptions.EndpointUri"/> tool route. Prefer the
/// DSL extension <c>.XPath(opts)</c> on any existing route — this shim is
/// only useful when you want a self-contained <c>RouteBuilder</c> to hand to
/// <c>context.AddRoutes(...)</c>.
/// </summary>
public sealed class XPathTool : RouteBuilder
{
    private const string InputSchema = """
        {
          "type": "object",
          "properties": {
            "xml":   { "type": "string", "description": "XML document to query." },
            "xpath": { "type": "string", "description": "XPath 1.0 expression (e.g. '//book[1]/title')." }
          },
          "required": ["xml", "xpath"],
          "additionalProperties": false
        }
        """;

    private readonly XPathOptions _options;

    /// <summary>Creates the tool with default options.</summary>
    public XPathTool() : this(new XPathOptions()) { }

    /// <summary>Creates the tool with the given options.</summary>
    public XPathTool(XPathOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <inheritdoc />
    protected override void Configure() =>
        From(_options.EndpointUri)
            .AsLlmTool(_options.ToolName)
                .Description("Extracts a value from an XML document via XPath 1.0. Returns the matched " +
                             "value as a string, or null when nothing matches.")
                .Input(InputSchema)
                .SideEffect(ToolSideEffect.ReadOnly)
                .Cost(ToolCostClass.Cheap)
            .Then()
            .XPath(_options);
}

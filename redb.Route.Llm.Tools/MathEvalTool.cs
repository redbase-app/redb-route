using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Llm.Abstractions.Tools;
using redb.Route.Llm.Extensions;

namespace redb.Route.Llm.Tools;

/// <summary>
/// Standalone <c>RouteBuilder</c> wrapper that mounts <see cref="MathEvalDsl.MathEval"/>
/// behind a <see cref="MathEvalOptions.EndpointUri"/> tool route. Prefer the
/// DSL extension <c>.MathEval(opts)</c> on any existing route — this shim is
/// only useful when you want a self-contained <c>RouteBuilder</c> to hand to
/// <c>context.AddRoutes(...)</c>.
/// </summary>
public sealed class MathEvalTool : RouteBuilder
{
    private const string InputSchema = """
        {
          "type": "object",
          "properties": {
            "expression": { "type": "string", "description": "Value expression supported by redb.Route's ExpressionResolver, e.g. '2 * (3 + 4)' or 'property.x + 1'." }
          },
          "required": ["expression"],
          "additionalProperties": false
        }
        """;

    private readonly MathEvalOptions _options;

    /// <summary>Creates the tool with default options.</summary>
    public MathEvalTool() : this(new MathEvalOptions()) { }

    /// <summary>Creates the tool with the given options.</summary>
    public MathEvalTool(MathEvalOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <inheritdoc />
    protected override void Configure() =>
        From(_options.EndpointUri)
            .AsLlmTool(_options.ToolName)
                .Description("Evaluates a value expression via redb.Route's ExpressionResolver " +
                             "(arithmetic, comparisons, ternary, jpath/property/header/body refs). " +
                             "Returns the result as JSON.")
                .Input(InputSchema)
                .SideEffect(ToolSideEffect.ReadOnly)
                .Cost(ToolCostClass.Cheap)
            .Then()
            .MathEval(_options);
}

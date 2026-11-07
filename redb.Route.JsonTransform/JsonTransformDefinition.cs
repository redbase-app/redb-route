using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Definitions;

namespace redb.Route.JsonTransform;

/// <summary>
/// The <c>&lt;transformJson&gt;</c> node: applies a JSONata specification to the JSON body. The
/// specification is compiled at route build — a missing file or a syntax error fails <c>Start()</c>
/// with the specification name — and cached by source. Declarative on purpose: the XML form reads
/// <see cref="Specification"/> and <see cref="Output"/>.
/// </summary>
public sealed class JsonTransformDefinition : ProcessorDefinition
{
    /// <summary>Creates the node.</summary>
    public JsonTransformDefinition(TextSource specification, JsonTransformOutput output = JsonTransformOutput.String)
    {
        Specification = specification ?? throw new ArgumentNullException(nameof(specification));
        Output = output;
    }

    /// <summary>Where the JSONata text comes from.</summary>
    public TextSource Specification { get; }

    /// <summary>JSON text or a <c>JsonNode</c> tree.</summary>
    public JsonTransformOutput Output { get; }

    /// <inheritdoc />
    public override IProcessor CreateProcessor(IRouteContext context)
    {
        // Context services first (UseJsonTransform), then the DI container (AddJsonTransform), then defaults.
        var options = context.GetService<JsonTransformOptions>()
            ?? context.GetServiceProvider()?.GetService(typeof(JsonTransformOptions)) as JsonTransformOptions
            ?? new JsonTransformOptions();
        // Same lookup as the template step and the validator: context resolver, then base directory.
        // A miss stays a specification failure, not an IO one — the step promises one exception type.
        TextSource specification;
        try
        {
            specification = ResourceResolution.Locate(context, Specification, options.BaseDirectory, "JSONata specification");
        }
        catch (FileNotFoundException ex)
        {
            throw new JsonTransformCompilationException(Specification.Name, ex.Message, ex);
        }

        var compiled = JsonTransformEngine.Compile(specification, options);
        return new JsonTransformProcessor(compiled, Output, options);
    }
}

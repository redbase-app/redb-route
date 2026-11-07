using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Definitions;

namespace redb.Route.Templates;

/// <summary>Where the rendered text goes.</summary>
public enum TemplateTarget
{
    /// <summary>Message body (and <c>ContentType</c> from the media type).</summary>
    Body,
    /// <summary>A message header named by <see cref="PayloadTemplateDefinition.TargetName"/>.</summary>
    Header,
    /// <summary>An exchange property named by <see cref="PayloadTemplateDefinition.TargetName"/>.</summary>
    Property,
}

/// <summary>
/// The <c>&lt;payload&gt;</c> node: renders a template into the body, a header or a property.
/// Compiles the template at route build — a missing file or a syntax error fails <c>Start()</c>
/// with the template name and position. Declarative on purpose: the XML form and Message History
/// read <see cref="Source"/>, <see cref="MediaType"/>, <see cref="Target"/> and <see cref="Args"/>.
/// </summary>
public sealed class PayloadTemplateDefinition : ProcessorDefinition
{
    /// <summary>Creates the node.</summary>
    public PayloadTemplateDefinition(TextSource source, MediaType mediaType, TemplateTarget target = TemplateTarget.Body, string? targetName = null, TemplateArgs? args = null)
    {
        Source = source ?? throw new ArgumentNullException(nameof(source));
        MediaType = mediaType;
        Target = target;
        if (target != TemplateTarget.Body)
            ArgumentException.ThrowIfNullOrWhiteSpace(targetName);
        TargetName = targetName;
        Args = args;
    }

    /// <summary>Template text origin.</summary>
    public TextSource Source { get; }

    /// <summary>Result type: drives escaping and <c>ContentType</c>.</summary>
    public MediaType MediaType { get; }

    /// <summary>Body, header or property.</summary>
    public TemplateTarget Target { get; }

    /// <summary>Header / property name when <see cref="Target"/> is not the body.</summary>
    public string? TargetName { get; }

    /// <summary>Named arguments evaluated before the render; <c>null</c> when none.</summary>
    public TemplateArgs? Args { get; }

    /// <inheritdoc />
    public override IProcessor CreateProcessor(IRouteContext context)
    {
        // Context services first (UseTemplates), then the DI container (AddRouteTemplates), then defaults.
        var options = context.GetService<RouteTemplateOptions>()
            ?? context.GetServiceProvider()?.GetService(typeof(RouteTemplateOptions)) as RouteTemplateOptions
            ?? new RouteTemplateOptions();
        // A relative path is looked up as every other file reference is: the context resolver (a
        // package keeps its templates where only that resolver knows), then the base directory.
        // A miss stays a template failure, not an IO one — the step promises one exception type.
        TextSource source;
        try
        {
            source = ResourceResolution.Locate(context, Source, options.BaseDirectory, "Template");
        }
        catch (FileNotFoundException ex)
        {
            throw new TemplateCompilationException(Source.Name, null, null, ex.Message, ex);
        }

        var compiled = TemplateEngine.Compile(source, options);
        return new PayloadTemplateProcessor(compiled, MediaType, Target, TargetName, Args, options);
    }
}

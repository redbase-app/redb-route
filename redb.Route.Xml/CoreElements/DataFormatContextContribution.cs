using System.Xml.Linq;
using redb.Route.Abstractions;
using redb.Route.Extensions;

namespace redb.Route.Xml.CoreElements;

/// <summary>
/// <c>&lt;dataFormat type="…" name="csv" delimiter=";"/&gt;</c> in <c>context.xml</c>: registers a data format on the
/// context, as <c>context.AddDataFormat(new CsvDataFormat(...))</c> does in code, so <c>&lt;marshal format="…"/&gt;</c> and
/// content-type dispatch find it. The type is the format's serializer (an <c>IMessageSerializer</c>); the other attributes
/// set its options, as on the per-step <c>&lt;marshal type="…"&gt;</c> form. The format answers to its own content type
/// and media types, and to each name in <c>name</c> (comma-separated).
/// </summary>
internal sealed class DataFormatContextContribution : IXmlContextContribution
{
    /// <summary>The one instance, in the core registry.</summary>
    internal static DataFormatContextContribution Instance { get; } = new();

    // Not constructible from outside: a host that discovers package contributions by scanning assemblies for
    // IXmlElementContribution types with a parameterless constructor (redb.Tsak) must not register a core element twice.
    private DataFormatContextContribution() { }

    private static readonly string[] OwnAttributes = ["type", "name", "id", "description"];

    /// <inheritdoc />
    public string Name => "dataFormat";

    /// <inheritdoc />
    public XmlElementKind Kind => XmlElementKind.ContextLevel;

    /// <inheritdoc />
    public ElementSpec Spec => CoreContributions.Specs[Name];

    /// <inheritdoc />
    public IRouteDefinition Apply(XElement element, IRouteDefinition current, XmlParseContext context)
    {
        context.AddError(element, "<dataFormat> registers a format for the whole context — put it inside <context>, not in a route.");
        return current;
    }

    /// <inheritdoc />
    public void ApplyContext(XElement element, Core.RouteContext context, XmlParseContext parseContext)
    {
        if (parseContext.RequiredAttr(element, "type") is not { } typeName)
            return;
        var options = element.Attributes().Where(a => !OwnAttributes.Contains(a.Name.LocalName)).ToList();
        if (CoreContributions.CreateSerializer(element, parseContext, typeName, options) is not { } serializer)
            return;

        var names = (parseContext.Attr(element, "name") ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        context.AddDataFormat(serializer, names);
    }
}

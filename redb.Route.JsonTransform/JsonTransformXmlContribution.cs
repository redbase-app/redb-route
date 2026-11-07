using System.Xml.Linq;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Xml;

namespace redb.Route.JsonTransform;

/// <summary>
/// The <c>&lt;transformJson&gt;</c> element (Route-XML Ф0 §4.1, V4 phase 05 package): JSONata
/// over the body, the specification either referenced by a locator (a path or
/// <c>assembly:Name/path</c>) or inline as the element's content (CDATA welcome) — exactly one
/// of the two, the Р5 rule.
/// </summary>
public sealed class JsonTransformXmlContribution : IXmlElementContribution
{
    /// <inheritdoc />
    public string Name => "transformJson";

    /// <inheritdoc />
    public XmlElementKind Kind => XmlElementKind.Step;

    /// <inheritdoc />
    public ElementSpec Spec => new(Name, Kind,
        [
            new AttributeSpec("spec", AttributeType.String) { Resource = true },
            // Ф0 §4.1 spells the values lowercase (string/node); parsing is case-insensitive.
            new AttributeSpec("output", AttributeType.Enum, EnumValues: ["string", "node"]),
        ],
        [], AllowsTextContent: true);

    /// <inheritdoc />
    public IRouteDefinition Apply(XElement element, IRouteDefinition current, XmlParseContext context)
    {
        var locator = context.Attr(element, "spec");
        var inline = Text(element);
        if ((locator is null) == (inline is null))
        {
            context.AddError(element, "<transformJson> takes either the spec locator or inline JSONata content, not both.");
            return current;
        }
        var output = JsonTransformOutput.String;
        if (context.Attr(element, "output") is { } outputName && !Enum.TryParse(outputName, ignoreCase: true, out output))
        {
            context.AddError(element, $"'{outputName}' is not a transform output ({string.Join(", ", Enum.GetNames<JsonTransformOutput>())}).");
            return current;
        }
        return locator is not null
            ? current.TransformJson(locator, output)
            : current.TransformJson(TextSource.Inline(inline!), output);
    }

    /// <inheritdoc />
    public void Print(XElement element, XmlCodeWriter code)
    {
        var output = element.Attribute("output")?.Value is { } o
            ? $"JsonTransformOutput.{Enum.Parse<JsonTransformOutput>(o, true)}"
            : null;
        var source = element.Attribute("spec")?.Value is { } locator
            ? XmlCodeWriter.Str(locator)
            : $"TextSource.Inline({XmlCodeWriter.Str(Text(element) ?? "")})";
        code.Verb(element, "TransformJson", output is null ? [source] : [source, output]);
    }

    private static string? Text(XElement element)
    {
        var text = string.Concat(element.Nodes().OfType<XText>().Select(t => t.Value));
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }
}

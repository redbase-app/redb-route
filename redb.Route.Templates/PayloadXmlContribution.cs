using System.Xml.Linq;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Xml;

namespace redb.Route.Templates;

/// <summary>
/// The <c>&lt;payload&gt;</c> element (Route-XML Ф0 §4.1, V4 phase 02 package): a Scriban
/// template — referenced by a locator or inline in the content — rendered into the body (the
/// default), a header or an exchange property; <c>&lt;arg name=… expr=…/&gt;</c> /
/// <c>&lt;arg name=… value=…/&gt;</c> children feed the template model.
/// </summary>
public sealed class PayloadXmlContribution : IXmlElementContribution
{
    /// <inheritdoc />
    public string Name => "payload";

    /// <inheritdoc />
    public XmlElementKind Kind => XmlElementKind.Step;

    /// <inheritdoc />
    public ElementSpec Spec => new(Name, Kind,
        [
            new AttributeSpec("template", AttributeType.String),
            // Ф0 §4.1 spells the values lowercase (json/xml/text); parsing is case-insensitive.
            new AttributeSpec("mediaType", AttributeType.Enum, Required: true, EnumValues: ["json", "xml", "text"]),
            new AttributeSpec("target", AttributeType.String),
        ],
        [ElementSpec.Child("arg", false,
            new AttributeSpec("name", AttributeType.String, Required: true),
            new AttributeSpec("value", AttributeType.String),
            new AttributeSpec("expr", AttributeType.Expression))],
        AllowsTextContent: true);

    /// <inheritdoc />
    public IRouteDefinition Apply(XElement element, IRouteDefinition current, XmlParseContext context)
    {
        var locator = context.Attr(element, "template");
        var inline = Text(element);
        if ((locator is null) == (inline is null))
        {
            context.AddError(element, "<payload> takes either the template locator or inline template content, not both.");
            return current;
        }
        var mediaTypeName = context.RequiredAttr(element, "mediaType");
        if (mediaTypeName is null)
            return current;
        if (!Enum.TryParse<MediaType>(mediaTypeName, ignoreCase: true, out var mediaType))
        {
            context.AddError(element, $"'{mediaTypeName}' is not a media type ({string.Join(", ", Enum.GetNames<MediaType>())}).");
            return current;
        }

        var arguments = new List<(string Name, string? Expr, string? Value)>();
        foreach (var child in element.Elements().Where(c => c.Name.LocalName == "arg"))
        {
            var name = context.RequiredAttr(child, "name");
            var pick = context.ExactlyOneOf(child, "value", "expr");
            if (name is null || pick is null)
                continue;
            arguments.Add((name,
                pick.Value.Name == "expr" ? pick.Value.Value : null,
                pick.Value.Name == "value" ? pick.Value.Value : null));
        }
        Action<TemplateArgs>? args = arguments.Count == 0 ? null : model =>
        {
            foreach (var (name, expr, value) in arguments)
            {
                if (expr is not null) model.Set(name, expr);
                else model.SetValue(name, value);
            }
        };

        var source = locator is null ? TextSource.Inline(inline!) : TextSource.FromLocator(locator);
        var target = context.Attr(element, "target") ?? "body";
        if (target == "body")
            return current.SetBodyTemplate(source, mediaType, args);
        if (target.StartsWith("header:", StringComparison.Ordinal))
            return current.SetHeaderTemplate(target["header:".Length..], source, mediaType, args);
        if (target.StartsWith("property:", StringComparison.Ordinal))
            return current.SetPropertyTemplate(target["property:".Length..], source, mediaType, args);
        context.AddError(element, $"target='{target}' is not valid — body (default), header:Name or property:Name.");
        return current;
    }

    /// <inheritdoc />
    public void Print(XElement element, XmlCodeWriter code)
    {
        var source = element.Attribute("template")?.Value is { } locator
            ? $"TextSource.FromLocator({XmlCodeWriter.Str(locator)})"
            : $"TextSource.Inline({XmlCodeWriter.Str(Text(element) ?? "")})";
        var mediaType = $"MediaType.{Enum.Parse<MediaType>(element.Attribute("mediaType")?.Value ?? "Json", true)}";
        var argCalls = element.Elements().Where(c => c.Name.LocalName == "arg")
            .Select(c => c.Attribute("expr")?.Value is { } expr
                ? $".Set({XmlCodeWriter.Str(c.Attribute("name")?.Value ?? "")}, {XmlCodeWriter.Str(expr)})"
                : $".SetValue({XmlCodeWriter.Str(c.Attribute("name")?.Value ?? "")}, {XmlCodeWriter.Str(c.Attribute("value")?.Value ?? "")})")
            .ToList();
        var args = argCalls.Count == 0 ? null : "a => a" + string.Concat(argCalls);

        var target = element.Attribute("target")?.Value ?? "body";
        if (target == "body")
            code.Verb(element, "SetBodyTemplate", args is null ? [source, mediaType] : [source, mediaType, args]);
        else if (target.StartsWith("header:", StringComparison.Ordinal))
            code.Verb(element, "SetHeaderTemplate", args is null
                ? [XmlCodeWriter.Str(target["header:".Length..]), source, mediaType]
                : [XmlCodeWriter.Str(target["header:".Length..]), source, mediaType, args]);
        else
            code.Verb(element, "SetPropertyTemplate", args is null
                ? [XmlCodeWriter.Str(target["property:".Length..]), source, mediaType]
                : [XmlCodeWriter.Str(target["property:".Length..]), source, mediaType, args]);
    }

    private static string? Text(XElement element)
    {
        var text = string.Concat(element.Nodes().OfType<XText>().Select(t => t.Value));
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }
}

using System.Xml.Linq;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Xml;

namespace redb.Route.Http.Rest;

/// <summary>
/// The <c>&lt;rest&gt;</c> element (Route-XML Ф0 §4.2, the REST DSL of this package): a
/// container-level Р21 contribution — it lives beside <c>&lt;route&gt;</c> and applies to the
/// route BUILDER, spawning the HTTP consumer routes the fluent <c>Rest(basePath, …)</c> would.
/// A verb child handles its request either with <c>to=</c> or with inline steps — one of the
/// two, never both.
/// </summary>
public sealed class RestXmlContribution : IXmlTopLevelContribution
{
    private static readonly string[] VerbNames = ["get", "post", "put", "delete", "patch", "head"];

    /// <inheritdoc />
    public string Name => "rest";

    /// <inheritdoc />
    public XmlElementKind Kind => XmlElementKind.TopLevel;

    /// <inheritdoc />
    public ElementSpec Spec => new(Name, Kind,
        [
            new AttributeSpec("path", AttributeType.String, Required: true),
            new AttributeSpec("host", AttributeType.String),
            new AttributeSpec("port", AttributeType.Int),
            new AttributeSpec("bindingMode", AttributeType.Enum, EnumValues: ["off", "json"]),
            new AttributeSpec("consumes", AttributeType.String),
            new AttributeSpec("produces", AttributeType.String),
            new AttributeSpec("openApi", AttributeType.Bool),
            new AttributeSpec("openApiPath", AttributeType.String),
            new AttributeSpec("title", AttributeType.String),
            new AttributeSpec("version", AttributeType.String),
        ],
        [.. VerbNames.Select(verb => new ElementSpec(verb, XmlElementKind.ConfigChild,
            [
                new AttributeSpec("path", AttributeType.String),
                new AttributeSpec("to", AttributeType.Uri),
                new AttributeSpec("consumes", AttributeType.String),
                new AttributeSpec("produces", AttributeType.String),
                new AttributeSpec("type", AttributeType.TypeName),
                new AttributeSpec("outType", AttributeType.TypeName),
                new AttributeSpec("bindingMode", AttributeType.Enum, EnumValues: ["off", "json"]),
            ],
            [], AllowsSteps: true))]);

    /// <inheritdoc />
    public IRouteDefinition Apply(XElement element, IRouteDefinition current, XmlParseContext context)
    {
        context.AddError(element, "<rest> is a container-level element — put it beside <route>, not inside one.");
        return current;
    }

    /// <inheritdoc />
    public void ApplyTopLevel(XElement element, RouteBuilder builder, XmlParseContext context)
    {
        var basePath = context.RequiredAttr(element, "path");
        if (basePath is null)
            return;
        var rest = builder.Rest(basePath, options =>
        {
            if (context.Attr(element, "host") is { } host) options.Host = host;
            if (context.Convert<int>(element, "port") is { } port) options.Port = port;
            if (ParseBinding(element, context) is { } binding) options.BindingMode = binding;
            if (context.Attr(element, "consumes") is { } consumes) options.Consumes = consumes;
            if (context.Attr(element, "produces") is { } produces) options.Produces = produces;
            if (context.Convert<bool>(element, "openApi") is { } openApi) options.OpenApi = openApi;
            if (context.Attr(element, "openApiPath") is { } openApiPath) options.OpenApiPath = openApiPath;
            if (context.Attr(element, "title") is { } title) options.Title = title;
            if (context.Attr(element, "version") is { } version) options.Version = version;
        });

        foreach (var child in element.Elements())
        {
            var verbName = child.Name.LocalName;
            if (!VerbNames.Contains(verbName))
            {
                context.AddError(child, $"<rest> accepts only verb children ({string.Join(", ", VerbNames)}), found <{verbName}>.");
                continue;
            }
            var verb = verbName switch
            {
                "get" => rest.Get(context.Attr(child, "path") ?? ""),
                "post" => rest.Post(context.Attr(child, "path") ?? ""),
                "put" => rest.Put(context.Attr(child, "path") ?? ""),
                "delete" => rest.Delete(context.Attr(child, "path") ?? ""),
                "patch" => rest.Patch(context.Attr(child, "path") ?? ""),
                _ => rest.Head(context.Attr(child, "path") ?? ""),
            };
            if (context.Attr(child, "consumes") is { } consumes) verb.Consumes(consumes);
            if (context.Attr(child, "produces") is { } produces) verb.Produces(produces);
            if (ParseBinding(child, context) is { } binding) verb.BindingMode(binding);
            if (context.Attr(child, "id") is { Length: > 0 } id) verb.Id(id);
            if (context.Attr(child, "description") is { Length: > 0 } description) verb.Description(description);
            if (!ApplyGenericType(child, context, "type", typeName => Closed(nameof(RestVerbDefinition.Type), typeName, verb))
                || !ApplyGenericType(child, context, "outType", typeName => Closed(nameof(RestVerbDefinition.OutType), typeName, verb)))
                continue;

            var target = context.Attr(child, "to");
            var steps = child.Elements().ToList();
            if (target is not null && steps.Count > 0)
            {
                context.AddError(child, $"<{verbName}> handles the request with to= or with inline steps — one of the two, not both.");
                continue;
            }
            if (target is null && steps.Count == 0)
            {
                context.AddError(child, $"<{verbName}> needs to= or at least one inline step.");
                continue;
            }
            if (target is not null)
            {
                context.NoteEndpointUri(child, target);
                verb.To(target);
            }
            else
            {
                var route = verb.Route();
                context.ParseSteps(child, route);
            }
        }
    }

    private static RestBindingMode? ParseBinding(XElement element, XmlParseContext context)
    {
        var value = context.Attr(element, "bindingMode");
        if (value is null)
            return null;
        if (Enum.TryParse<RestBindingMode>(value, ignoreCase: true, out var mode))
            return mode;
        context.AddError(element, $"'{value}' is not a binding mode (off, json).");
        return null;
    }

    /// <summary>Closes Type&lt;T&gt;()/OutType&lt;T&gt;() over an XML type name (cold path).</summary>
    private static bool ApplyGenericType(XElement child, XmlParseContext context, string attribute,
        Action<Type> apply)
    {
        var typeName = context.Attr(child, attribute);
        if (typeName is null)
            return true;
        var resolver = context.RouteContext.GetService<IBeanTypeResolver>()
            ?? Components.Bean.DefaultBeanTypeResolver.Instance;
        var type = resolver.Resolve(typeName);
        if (type is null)
        {
            context.AddError(child, $"{attribute}='{typeName}' was not found in the loaded assemblies.");
            return false;
        }
        apply(type);
        return true;
    }

    private static void Closed(string methodName, Type type, RestVerbDefinition verb)
        => typeof(RestVerbDefinition).GetMethod(methodName)!.MakeGenericMethod(type).Invoke(verb, null);

    /// <inheritdoc />
    public void Print(XElement element, XmlCodeWriter code)
    {
        var optionLines = new List<string>();
        void Opt(string attr, Func<string, string> render)
        {
            if (element.Attribute(attr)?.Value is { } value) optionLines.Add(render(value));
        }
        Opt("host", v => $"options.Host = {XmlCodeWriter.Str(v)};");
        Opt("port", v => $"options.Port = {v};");
        Opt("bindingMode", v => $"options.BindingMode = RestBindingMode.{Enum.Parse<RestBindingMode>(v, true)};");
        Opt("consumes", v => $"options.Consumes = {XmlCodeWriter.Str(v)};");
        Opt("produces", v => $"options.Produces = {XmlCodeWriter.Str(v)};");
        Opt("openApi", v => $"options.OpenApi = {XmlCodeWriter.Bool(bool.Parse(v))};");
        Opt("openApiPath", v => $"options.OpenApiPath = {XmlCodeWriter.Str(v)};");
        Opt("title", v => $"options.Title = {XmlCodeWriter.Str(v)};");
        Opt("version", v => $"options.Version = {XmlCodeWriter.Str(v)};");

        var declaration = optionLines.Count == 0
            ? $"Rest({XmlCodeWriter.Str(element.Attribute("path")?.Value ?? "")})"
            : $"Rest({XmlCodeWriter.Str(element.Attribute("path")?.Value ?? "")}, options => {{ {string.Join(" ", optionLines)} }})";
        var rest = code.PushRoot(declaration, element, "rest");
        code.PopReceiver();

        foreach (var child in element.Elements().Where(c => VerbNames.Contains(c.Name.LocalName)))
        {
            var verbCall = char.ToUpperInvariant(child.Name.LocalName[0]) + child.Name.LocalName[1..];
            var chain = new System.Text.StringBuilder($"{rest}.{verbCall}({XmlCodeWriter.Str(child.Attribute("path")?.Value ?? "")})");
            if (child.Attribute("consumes")?.Value is { } consumes) chain.Append($".Consumes({XmlCodeWriter.Str(consumes)})");
            if (child.Attribute("produces")?.Value is { } produces) chain.Append($".Produces({XmlCodeWriter.Str(produces)})");
            if (child.Attribute("bindingMode")?.Value is { } binding) chain.Append($".BindingMode(RestBindingMode.{Enum.Parse<RestBindingMode>(binding, true)})");
            if (child.Attribute("id")?.Value is { Length: > 0 } id) chain.Append($".Id({XmlCodeWriter.Str(id)})");
            if (child.Attribute("description")?.Value is { Length: > 0 } description) chain.Append($".Description({XmlCodeWriter.Str(description)})");
            if (child.Attribute("type")?.Value is { } requestType) chain.Append($".Type<{TypeArg(requestType)}>()");
            if (child.Attribute("outType")?.Value is { } responseType) chain.Append($".OutType<{TypeArg(responseType)}>()");

            if (child.Attribute("to")?.Value is { } target)
            {
                code.MapTo(child);
                code.Line($"{chain}.To({XmlCodeWriter.Str(target)});");
                continue;
            }
            code.MapTo(child);
            var route = code.PushRoot($"{chain}.Route()", child, "handler");
            code.PrintSteps(child);
            code.PopReceiver();
            _ = route;
        }
    }

    private static string TypeArg(string typeName)
    {
        var comma = typeName.IndexOf(',');
        return "global::" + (comma >= 0 ? typeName[..comma] : typeName).Trim();
    }
}

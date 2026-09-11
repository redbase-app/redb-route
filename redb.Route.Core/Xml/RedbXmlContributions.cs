using System.Globalization;
using System.Xml.Linq;
using redb.Route.Abstractions;
using redb.Route.Components.Bean;
using redb.Route.RedbCore.Extensions;
using redb.Route.RedbCore.Transactions;
using redb.Route.Xml;

namespace redb.Route.RedbCore.Xml;

/// <summary>
/// The redb storage elements of the route markup (Route-XML, Р21 contributions of this
/// package): <c>&lt;redbGet&gt;</c>, <c>&lt;redbSave&gt;</c>, <c>&lt;redbDelete&gt;</c>,
/// <c>&lt;beginRedbTransaction&gt;</c> in routes and the <c>&lt;redb&gt;</c> bridge block in
/// <c>context.xml</c>. Parse, schema shape and C# printing live together per element.
/// </summary>
internal static class RedbXml
{
    internal static Type? ResolveType(XElement element, string typeName, XmlParseContext context)
    {
        var resolver = context.RouteContext.GetService<IBeanTypeResolver>()
            ?? DefaultBeanTypeResolver.Instance;
        var type = resolver.Resolve(typeName);
        if (type is null)
            context.AddError(element, $"props type '{typeName}' was not found in the loaded assemblies.");
        return type;
    }

    internal static string? OptionalArgs(XElement element, out int? depth, out string? storage, out string? target)
    {
        depth = element.Attribute("depth")?.Value is { } d
            ? int.Parse(d, CultureInfo.InvariantCulture)
            : null;
        storage = element.Attribute("storage")?.Value;
        target = element.Attribute("target")?.Value;
        return null;
    }

    /// <summary>The element's text content (CDATA included), null when there is none.</summary>
    internal static string? Text(XElement element)
    {
        var text = string.Concat(element.Nodes().OfType<XText>().Select(t => t.Value));
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }
}

/// <summary>The <c>&lt;redbGet&gt;</c> element: typed load with <c>type=</c>, raw-JSON load without.</summary>
public sealed class RedbGetXmlContribution : IXmlElementContribution
{
    /// <inheritdoc />
    public IReadOnlyList<string> GeneratedUsings => ["redb.Route.RedbCore.Extensions"];

    /// <inheritdoc />
    public string Name => "redbGet";

    /// <inheritdoc />
    public XmlElementKind Kind => XmlElementKind.Step;

    /// <inheritdoc />
    public ElementSpec Spec => ElementSpec.Leaf(Name,
        new AttributeSpec("id", AttributeType.Expression, Required: true),
        new AttributeSpec("type", AttributeType.TypeName),
        new AttributeSpec("depth", AttributeType.Int),
        new AttributeSpec("storage", AttributeType.String),
        new AttributeSpec("target", AttributeType.String));

    /// <inheritdoc />
    public IRouteDefinition Apply(XElement element, IRouteDefinition current, XmlParseContext context)
    {
        var id = context.RequiredAttr(element, "id");
        if (id is null)
            return current;
        RedbXml.OptionalArgs(element, out var depth, out var storage, out var target);

        if (element.Attribute("type")?.Value is { } typeName)
        {
            var type = RedbXml.ResolveType(element, typeName, context);
            return type is null
                ? current
                : current.RedbGet(type, id, depth ?? 10, storage, target);
        }
        return current.RedbGetJson(id, depth ?? 10, storage, target);
    }

    /// <inheritdoc />
    public void Print(XElement element, XmlCodeWriter code)
    {
        RedbXml.OptionalArgs(element, out var depth, out var storage, out var target);
        var args = new List<string>();
        if (element.Attribute("type")?.Value is { } typeName)
            args.Add(XmlCodeWriter.TypeRef(typeName));
        args.Add(XmlCodeWriter.Str(element.Attribute("id")!.Value));
        if (depth is { } d) args.Add($"depth: {d}");
        if (storage is not null) args.Add($"storage: {XmlCodeWriter.Str(storage)}");
        if (target is not null) args.Add($"target: {XmlCodeWriter.Str(target)}");
        code.Verb(element, element.Attribute("type") is null ? "RedbGetJson" : "RedbGet", [.. args]);
    }
}

/// <summary>The <c>&lt;redbSave&gt;</c> element: saves the body (JSON is materialized via <c>type=</c>).</summary>
public sealed class RedbSaveXmlContribution : IXmlElementContribution
{
    /// <inheritdoc />
    public IReadOnlyList<string> GeneratedUsings => ["redb.Route.RedbCore.Extensions"];

    /// <inheritdoc />
    public string Name => "redbSave";

    /// <inheritdoc />
    public XmlElementKind Kind => XmlElementKind.Step;

    /// <inheritdoc />
    public ElementSpec Spec => ElementSpec.Leaf(Name,
        new AttributeSpec("type", AttributeType.TypeName),
        new AttributeSpec("byUnique", AttributeType.Bool),
        new AttributeSpec("storage", AttributeType.String));

    /// <inheritdoc />
    public IRouteDefinition Apply(XElement element, IRouteDefinition current, XmlParseContext context)
    {
        Type? type = null;
        if (element.Attribute("type")?.Value is { } typeName)
        {
            type = RedbXml.ResolveType(element, typeName, context);
            if (type is null)
                return current;
        }
        var byUnique = context.Convert<bool>(element, "byUnique") ?? false;
        if (byUnique && type is null)
        {
            context.AddError(element, "byUnique=\"true\" needs type= — the unique-key save is typed.");
            return current;
        }
        return current.RedbSave(type, byUnique, element.Attribute("storage")?.Value);
    }

    /// <inheritdoc />
    public void Print(XElement element, XmlCodeWriter code)
    {
        var args = new List<string>();
        if (element.Attribute("type")?.Value is { } typeName)
            args.Add(XmlCodeWriter.TypeRef(typeName));
        if (element.Attribute("byUnique")?.Value == "true") args.Add("byUnique: true");
        if (element.Attribute("storage")?.Value is { } storage)
            args.Add($"storage: {XmlCodeWriter.Str(storage)}");
        code.Verb(element, "RedbSave", [.. args]);
    }
}

/// <summary>
/// The <c>&lt;redbQuery&gt;</c> element: a server-side props query — the <c>where</c> string
/// rides the engine's ONE expression AST into redb LINQ, <c>filter="#spec"</c> is the
/// full-LINQ escape hatch. A condition the translator refuses is a positioned load error.
/// </summary>
public sealed class RedbQueryXmlContribution : IXmlElementContribution
{
    /// <inheritdoc />
    public IReadOnlyList<string> GeneratedUsings => ["redb.Route.RedbCore.Extensions"];

    /// <inheritdoc />
    public string Name => "redbQuery";

    /// <inheritdoc />
    public XmlElementKind Kind => XmlElementKind.Step;

    /// <inheritdoc />
    public ElementSpec Spec => new(Name, Kind,
        [
            new AttributeSpec("type", AttributeType.TypeName, Required: true),
            new AttributeSpec("where", AttributeType.Expression),
            new AttributeSpec("orderBy", AttributeType.String),
            new AttributeSpec("descending", AttributeType.Bool),
            new AttributeSpec("take", AttributeType.Int),
            new AttributeSpec("skip", AttributeType.Int),
            new AttributeSpec("filter", AttributeType.Reference),
            new AttributeSpec("storage", AttributeType.String),
            new AttributeSpec("target", AttributeType.String),
        ],
        // The condition may live as the element's text instead of the where attribute — a
        // CDATA block frees it from &lt; escaping. Exactly one of the two forms.
        [], AllowsTextContent: true);

    /// <inheritdoc />
    public IRouteDefinition Apply(XElement element, IRouteDefinition current, XmlParseContext context)
    {
        var typeName = context.RequiredAttr(element, "type");
        if (typeName is null)
            return current;
        var type = RedbXml.ResolveType(element, typeName, context);
        if (type is null)
            return current;
        var whereAttribute = context.Attr(element, "where");
        var whereInline = RedbXml.Text(element);
        if (whereAttribute is not null && whereInline is not null)
        {
            context.AddError(element,
                "<redbQuery> takes the condition either as where= or as the element's text (CDATA welcome), not both.");
            return current;
        }
        try
        {
            return current.RedbQuery(type,
                where: whereAttribute ?? whereInline,
                orderBy: context.Attr(element, "orderBy"),
                descending: context.Convert<bool>(element, "descending") ?? false,
                take: context.Convert<int>(element, "take"),
                skip: context.Convert<int>(element, "skip"),
                filter: context.Attr(element, "filter"),
                storage: context.Attr(element, "storage"),
                target: context.Attr(element, "target"));
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            // The translator's refusals (and the unbounded-scan guard) become positioned
            // document errors — collected with the rest, not a crash mid-parse.
            context.AddError(element, ex.Message);
            return current;
        }
    }

    /// <inheritdoc />
    public void Print(XElement element, XmlCodeWriter code)
    {
        var args = new List<string> { XmlCodeWriter.TypeRef(element.Attribute("type")!.Value) };
        if ((element.Attribute("where")?.Value ?? RedbXml.Text(element)) is { } where)
            args.Add($"where: {XmlCodeWriter.Str(where)}");
        if (element.Attribute("orderBy")?.Value is { } orderBy) args.Add($"orderBy: {XmlCodeWriter.Str(orderBy)}");
        if (element.Attribute("descending")?.Value == "true") args.Add("descending: true");
        if (element.Attribute("take")?.Value is { } take) args.Add($"take: {take}");
        if (element.Attribute("skip")?.Value is { } skip) args.Add($"skip: {skip}");
        if (element.Attribute("filter")?.Value is { } filter) args.Add($"filter: {XmlCodeWriter.Str(filter)}");
        if (element.Attribute("storage")?.Value is { } storage) args.Add($"storage: {XmlCodeWriter.Str(storage)}");
        if (element.Attribute("target")?.Value is { } target) args.Add($"target: {XmlCodeWriter.Str(target)}");
        code.Verb(element, "RedbQuery", [.. args]);
    }
}

/// <summary>The <c>&lt;redbDelete&gt;</c> element: deletes by id, no props type needed.</summary>
public sealed class RedbDeleteXmlContribution : IXmlElementContribution
{
    /// <inheritdoc />
    public IReadOnlyList<string> GeneratedUsings => ["redb.Route.RedbCore.Extensions"];

    /// <inheritdoc />
    public string Name => "redbDelete";

    /// <inheritdoc />
    public XmlElementKind Kind => XmlElementKind.Step;

    /// <inheritdoc />
    public ElementSpec Spec => ElementSpec.Leaf(Name,
        new AttributeSpec("id", AttributeType.Expression, Required: true),
        new AttributeSpec("storage", AttributeType.String));

    /// <inheritdoc />
    public IRouteDefinition Apply(XElement element, IRouteDefinition current, XmlParseContext context)
    {
        var id = context.RequiredAttr(element, "id");
        return id is null ? current : current.RedbDelete(id, element.Attribute("storage")?.Value);
    }

    /// <inheritdoc />
    public void Print(XElement element, XmlCodeWriter code)
    {
        var args = new List<string> { XmlCodeWriter.Str(element.Attribute("id")!.Value) };
        if (element.Attribute("storage")?.Value is { } storage)
            args.Add($"storage: {XmlCodeWriter.Str(storage)}");
        code.Verb(element, "RedbDelete", [.. args]);
    }
}

/// <summary>
/// The <c>&lt;beginRedbTransaction&gt;</c> element: opens the ambient redb transaction the
/// transacted pipeline commits or rolls back with the exchange.
/// </summary>
public sealed class BeginRedbTransactionXmlContribution : IXmlElementContribution
{
    /// <inheritdoc />
    public IReadOnlyList<string> GeneratedUsings => ["redb.Route.RedbCore.Transactions"];

    /// <inheritdoc />
    public string Name => "beginRedbTransaction";

    /// <inheritdoc />
    public XmlElementKind Kind => XmlElementKind.Step;

    /// <inheritdoc />
    public ElementSpec Spec => ElementSpec.Leaf(Name,
        new AttributeSpec("storage", AttributeType.String));

    /// <inheritdoc />
    public IRouteDefinition Apply(XElement element, IRouteDefinition current, XmlParseContext context)
        => element.Attribute("storage")?.Value is { } storage
            ? current.BeginRedbTransaction(storage)
            : current.BeginRedbTransaction();

    /// <inheritdoc />
    public void Print(XElement element, XmlCodeWriter code)
        => code.Verb(element, "BeginRedbTransaction",
            element.Attribute("storage")?.Value is { } storage ? [XmlCodeWriter.Str(storage)] : []);
}

/// <summary>
/// The <c>&lt;redb&gt;</c> block of <c>context.xml</c>: scheme synchronization at context
/// startup and named idempotent repositories — the context-level face of the bridge.
/// </summary>
public sealed class RedbContextXmlContribution : IXmlContextContribution
{
    /// <inheritdoc />
    public string Name => "redb";

    /// <inheritdoc />
    public XmlElementKind Kind => XmlElementKind.ContextLevel;

    /// <inheritdoc />
    public ElementSpec Spec => new(Name, Kind,
        [new AttributeSpec("storage", AttributeType.String)],
        [
            ElementSpec.Child("syncScheme", allowsSteps: false,
                new AttributeSpec("type", AttributeType.TypeName, Required: true)),
            ElementSpec.Child("idempotentRepository", allowsSteps: false,
                new AttributeSpec("name", AttributeType.String, Required: true),
                new AttributeSpec("ttl", AttributeType.Duration),
                new AttributeSpec("processorName", AttributeType.String)),
        ],
        AllowsSteps: false);

    /// <inheritdoc />
    public IRouteDefinition Apply(XElement element, IRouteDefinition current, XmlParseContext context)
    {
        context.AddError(element, "<redb> is a context-level block — put it inside <context>, not in a route.");
        return current;
    }

    /// <inheritdoc />
    public void ApplyContext(XElement element, Route.Core.RouteContext context, XmlParseContext parseContext)
    {
        var storage = element.Attribute("storage")?.Value;
        foreach (var child in element.Elements())
        {
            switch (child.Name.LocalName)
            {
                case "syncScheme":
                    if (parseContext.RequiredAttr(child, "type") is { } typeName
                        && RedbXml.ResolveType(child, typeName, parseContext) is { } type)
                        context.SyncRedbScheme(type, storage);
                    break;

                case "idempotentRepository":
                    if (parseContext.RequiredAttr(child, "name") is { } name)
                        context.AddRedbIdempotentRepository(name,
                            parseContext.Convert<TimeSpan>(child, "ttl"),
                            parseContext.Attr(child, "processorName"));
                    break;

                default:
                    parseContext.AddError(child,
                        $"unknown <redb> child <{child.Name.LocalName}> (valid: <syncScheme>, <idempotentRepository>).");
                    break;
            }
        }
    }
}

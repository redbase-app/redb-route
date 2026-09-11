using System.Xml.Linq;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Xml;

namespace redb.Route.Cache;

/// <summary>
/// The <c>&lt;cache&gt;</c> element (Route-XML Ф0 §4.2, V4 phase 08 package): the Р21
/// contribution this package registers via <see cref="XmlRouteLoaderOptions.Extensions"/> —
/// parse, schema shape and C# printing in one place, so the loader, the XSD and the generator
/// see the element together or not at all.
/// </summary>
public sealed class CacheXmlContribution : IXmlElementContribution
{
    /// <inheritdoc />
    public string Name => "cache";

    /// <inheritdoc />
    public XmlElementKind Kind => XmlElementKind.Scope;

    /// <inheritdoc />
    public ElementSpec Spec => new(Name, Kind,
        [
            new AttributeSpec("key", AttributeType.Expression),
            new AttributeSpec("keyFromBody", AttributeType.Bool),
            new AttributeSpec("ttl", AttributeType.Duration),
            new AttributeSpec("sliding", AttributeType.Duration),
            new AttributeSpec("region", AttributeType.String),
            new AttributeSpec("cacheHeaders", AttributeType.Bool),
            new AttributeSpec("provider", AttributeType.Enum, EnumValues: ["memory", "distributed"]),
        ],
        [], AllowsSteps: true);

    /// <inheritdoc />
    public IRouteDefinition Apply(XElement element, IRouteDefinition current, XmlParseContext context)
    {
        var pick = context.ExactlyOneOf(element, "key", "keyFromBody");
        if (pick is null)
            return current;
        var ttl = context.Convert<TimeSpan>(element, "ttl");
        var scope = pick.Value.Name == "key"
            ? current.Cache(pick.Value.Value, ttl)
            : current.Cache(static _ => string.Empty, ttl).KeyFromBody();
        if (context.Convert<TimeSpan>(element, "sliding") is { } sliding) scope.SlidingExpiration(sliding);
        if (context.Attr(element, "region") is { } region) scope.Region(region);
        if (context.Convert<bool>(element, "cacheHeaders") == true) scope.CacheHeaders();
        switch (context.Attr(element, "provider"))
        {
            case "distributed": scope.Distributed(); break;
            case "memory": scope.InMemory(); break;
            case null: break;
            case { } other:
                context.AddError(element, $"'{other}' is not a cache provider (memory, distributed).");
                return current;
        }
        context.ParseSteps(element, scope);
        return scope.EndCache();
    }

    /// <inheritdoc />
    public void Print(XElement element, XmlCodeWriter code)
    {
        var key = element.Attribute("key")?.Value;
        var ttl = element.Attribute("ttl")?.Value is { } t
            ? XmlCodeWriter.Ts(TimeSpan.Parse(t, System.Globalization.CultureInfo.InvariantCulture))
            : null;
        var first = key is not null ? XmlCodeWriter.Str(key) : "static _ => string.Empty";
        string[] args = ttl is null ? [first] : [first, ttl];
        code.Scope(element, "Cache", args, "EndCache", body: w =>
        {
            if (key is null) w.Config("KeyFromBody()");
            if (element.Attribute("sliding")?.Value is { } sliding)
                w.Config($"SlidingExpiration({XmlCodeWriter.Ts(TimeSpan.Parse(sliding, System.Globalization.CultureInfo.InvariantCulture))})");
            if (element.Attribute("region")?.Value is { } region) w.Config($"Region({XmlCodeWriter.Str(region)})");
            if (element.Attribute("cacheHeaders")?.Value == "true") w.Config("CacheHeaders()");
            switch (element.Attribute("provider")?.Value)
            {
                case "distributed": w.Config("Distributed()"); break;
                case "memory": w.Config("InMemory()"); break;
            }
            w.PrintSteps(element);
        });
    }
}

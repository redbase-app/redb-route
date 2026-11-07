using System.Xml.Linq;
using redb.Route.Abstractions;
using redb.Route.Components.Bean;
using redb.Route.Core;

namespace redb.Route.Xml.CoreElements;

/// <summary>
/// <c>&lt;routeBuilder type="Orders.Routes.OrdersRoute, Orders"/&gt;</c> in <c>context.xml</c>: adds a C# route builder to the
/// context, as <c>context.AddRoutes(new OrdersRoute())</c> does in code, so one package carries routes in markup and in the
/// DSL, in one context. The type resolves through the context's type resolver, the same lookup as <c>&lt;bean type&gt;</c>
/// (in a Tsak package: the package's own assemblies first). It must be a public, non-abstract class deriving from
/// <see cref="RouteBuilder"/> with a public parameterless constructor, the rule of <c>AddRoutes(Assembly[])</c>.
/// </summary>
internal sealed class RouteBuilderContextContribution : IXmlContextContribution
{
    /// <summary>The one instance, in the core registry.</summary>
    internal static RouteBuilderContextContribution Instance { get; } = new();

    // Not constructible from outside: a host that discovers package contributions by scanning assemblies for
    // IXmlElementContribution types with a parameterless constructor (redb.Tsak) must not register a core element twice.
    private RouteBuilderContextContribution() { }

    /// <inheritdoc />
    public string Name => "routeBuilder";

    /// <inheritdoc />
    public XmlElementKind Kind => XmlElementKind.ContextLevel;

    /// <inheritdoc />
    public ElementSpec Spec => CoreContributions.Specs[Name];

    /// <inheritdoc />
    public IRouteDefinition Apply(XElement element, IRouteDefinition current, XmlParseContext context)
    {
        context.AddError(element, "<routeBuilder> adds C# routes to the whole context — put it inside <context>, not in a route.");
        return current;
    }

    /// <inheritdoc />
    public void ApplyContext(XElement element, RouteContext context, XmlParseContext parseContext)
    {
        if (parseContext.RequiredAttr(element, "type") is not { } typeName)
            return;
        var resolver = context.GetService<IBeanTypeResolver>() ?? DefaultBeanTypeResolver.Instance;
        if (resolver.Resolve(typeName) is not { } type)
        {
            parseContext.AddError(element, $"<routeBuilder>: type '{typeName}' was not found in the loaded assemblies.");
            return;
        }
        if (Refusal(type) is { } reason)
        {
            parseContext.AddError(element, $"<routeBuilder>: '{typeName}' {reason}");
            return;
        }

        // The same builder twice compiles its routes twice and fails only at Start, on a duplicate route id.
        if (element.ElementsBeforeSelf().Any(e => e.Name == element.Name
                && e.Attribute("type")?.Value is { Length: > 0 } earlier && resolver.Resolve(earlier) == type)
            || context.RouteBuilders.Any(b => b.GetType() == type))
        {
            parseContext.AddError(element, $"<routeBuilder>: '{type.FullName}' is already added to this context.");
            return;
        }

        RouteBuilder builder;
        try
        {
            builder = (RouteBuilder)Activator.CreateInstance(type)!;
        }
        catch (System.Reflection.TargetInvocationException ex)
        {
            parseContext.AddError(element,
                $"<routeBuilder>: '{typeName}' threw from its constructor: {(ex.InnerException ?? ex).Message}");
            return;
        }
        // Added once the whole document is valid: a context.xml with an error further down adds no routes (in Tsak the
        // module faults, and its routes must not run in the context it shares).
        parseContext.WhenValid(() => context.AddRoutes(builder));
    }

    /// <summary>
    /// Why <paramref name="type"/> cannot be a markup route builder, or null when it can. The base type is matched by full
    /// name, so the packaging check can hold a type loaded in its own load context against the same rule.
    /// </summary>
    internal static string? Refusal(Type type)
    {
        if (!DerivesFrom(type, typeof(RouteBuilder).FullName!))
            return $"does not derive from {typeof(RouteBuilder).FullName}.";
        if (type.FullName == typeof(InlineRouteBuilder).FullName)
            return "is the inline builder of AddRoutes(Action<InlineRouteBuilder>), not a route builder of its own.";
        if (!type.IsVisible)
            return "is not public.";
        if (type.IsAbstract)
            return "is abstract.";
        if (type.ContainsGenericParameters)
            return "is an open generic type.";
        if (type.GetConstructor(Type.EmptyTypes) is null)
            return "has no public parameterless constructor; a builder named in markup is created without arguments. " +
                   "Read configuration through {{key}} properties and reach beans by their registry name.";
        return null;
    }

    private static bool DerivesFrom(Type type, string baseFullName)
    {
        for (var current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (current.FullName == baseFullName)
                return true;
        }
        return false;
    }
}

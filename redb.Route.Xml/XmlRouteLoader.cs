using System.Reflection;
using System.Xml.Linq;
using Microsoft.Extensions.DependencyInjection;
using redb.Route.Abstractions;
using redb.Route.Components.Bean;
using redb.Route.Core;

namespace redb.Route.Xml;

/// <summary>
/// Loads <c>.route.xml</c> documents into a <see cref="RouteContext"/> through the existing
/// fluent DSL — no second engine (Р1). The whole document is parsed eagerly at
/// <see cref="Load"/>: beans first (so <c>#name</c> references resolve), then every route;
/// every error of the document — unknown elements, malformed expressions, unregistered URI
/// schemes — is collected in one pass and thrown as one <see cref="XmlRouteException"/> with
/// file positions. Nothing is registered when the document has errors.
/// </summary>
public sealed class XmlRouteLoader
{
    /// <summary>The route-format XML namespace; the major part changes only on a breaking format change.</summary>
    public const string Namespace = "urn:redb:route:1.0";

    /// <summary>The format version this loader reads (Ф4 §3.2): same major, minor not above.</summary>
    internal const int SupportedMajor = 1;
    internal const int SupportedMinor = 0;

    /// <summary>
    /// The §3.2 version policy on the root namespace: <c>urn:redb:route:{major}.{minor}</c> —
    /// the major must match, the minor must not exceed what this loader supports; a newer
    /// document says exactly what to update instead of failing on the first unknown element.
    /// Returns null when the namespace is not ours at all (the caller keeps its own message).
    /// </summary>
    internal static string? VersionError(string namespaceName)
    {
        const string prefix = "urn:redb:route:";
        if (!namespaceName.StartsWith(prefix, StringComparison.Ordinal))
            return null;
        var version = namespaceName[prefix.Length..];
        var dot = version.IndexOf('.');
        if (dot <= 0
            || !int.TryParse(version[..dot], out var major)
            || !int.TryParse(version[(dot + 1)..], out var minor))
            return $"'{namespaceName}' is not a valid route-format namespace (expected urn:redb:route:MAJOR.MINOR).";
        if (major != SupportedMajor)
            return $"the document is format {major}.{minor}; this loader reads major version {SupportedMajor} " +
                   $"({Namespace}) — a different major is a different format.";
        if (minor > SupportedMinor)
            return $"the document needs format {major}.{minor}; this loader supports up to " +
                   $"{SupportedMajor}.{SupportedMinor} — update the redb.Route.Xml package.";
        return null;
    }

    private readonly RouteContext _context;
    private readonly ElementRegistry _registry;
    private readonly bool _validate;
    private readonly bool _hasExtensions;

    /// <summary>Creates a loader for the given context.</summary>
    public XmlRouteLoader(RouteContext context, XmlRouteLoaderOptions? options = null)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _registry = ElementRegistry.CreateDefault(options?.Extensions);
        _validate = options?.ValidateAgainstSchema ?? true;
        _hasExtensions = options?.Extensions is { Count: > 0 };
    }

    /// <summary>
    /// The §3.4 pass: schema findings appended AFTER the parse, skipping lines the parser
    /// already reported — its messages (did-you-mean, exact rules) win on a shared line.
    /// </summary>
    internal void ValidateAgainstSchema(XDocument document, string sourceName, XmlParseContext ctx)
    {
        if (!_validate)
            return;
        var schema = _hasExtensions ? XmlRouteSchema.For(_registry) : XmlRouteSchema.CoreSet;
        foreach (var (line, column, message) in XmlRouteSchema.Validate(document, schema))
        {
            if (!ctx.ErrorLines.Contains(line))
                ctx.AddRawError($"{sourceName}({line},{column}): [schema] {message}");
        }
    }

    /// <summary>Parses and registers every route of the document. See the class remarks.</summary>
    public void Load(XDocument document, string? sourceName = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        sourceName ??= "<inline>";
        var ctx = new XmlParseContext(_context, _registry, sourceName);

        var root = document.Root;
        if (root is null || root.Name.LocalName != "routes")
        {
            throw new XmlRouteException(sourceName,
                [$"the document root must be <routes xmlns=\"{Namespace}\">, found <{root?.Name.LocalName ?? "nothing"}>."]);
        }
        if (root.Name.NamespaceName != Namespace)
        {
            throw new XmlRouteException(sourceName,
                [VersionError(root.Name.NamespaceName)
                 ?? $"unexpected root namespace '{root.Name.NamespaceName}'; this loader reads '{Namespace}'."]);
        }
        ctx.CheckElementNamespaces(root);

        // Section order (Ф0 §2): beans before routes, so registry references resolve.
        foreach (var bean in root.Elements().Where(e => e.Name.LocalName == "bean"))
            BeanSection.Declare(bean, ctx);

        var containerElements = new List<XElement>();
        var topLevelElements = new List<(XElement Element, IXmlTopLevelContribution Contribution)>();
        foreach (var element in root.Elements())
        {
            if (element.Name.LocalName is "bean" or "route")
                continue;
            if (element.Name.LocalName
                is "onException" or "intercept" or "interceptFrom" or "interceptSendToEndpoint" or "onCompletion")
            {
                // Ф0 §4.2: declared on the builder — they apply to every route of the file.
                containerElements.Add(element);
                continue;
            }
            if (ctx.Registry.Find(element.Name.LocalName) is IXmlTopLevelContribution topLevel
                && topLevel.Kind == XmlElementKind.TopLevel)
            {
                // A package's container-level element (the REST DSL): applied to the builder.
                topLevelElements.Add((element, topLevel));
                continue;
            }
            ctx.AddError(element, ctx.Registry.Find(element.Name.LocalName) is null
                ? $"unknown container element <{element.Name.LocalName}>."
                : $"<{element.Name.LocalName}> is not valid at the container level.");
        }

        // The parse runs inside the builder's Configure — InternalBuild clears and rebuilds a
        // builder, so definitions must be born there. Building eagerly here surfaces every error
        // at Load; IsBuilt then keeps Start() from re-running the parse.
        var routeElements = root.Elements().Where(e => e.Name.LocalName == "route").ToList();
        var builder = new XmlRoutesBuilder(b =>
        {
            foreach (var element in containerElements)
                ParseContainerHandler(element, b, ctx);
            foreach (var (element, contribution) in topLevelElements)
            {
                try
                {
                    contribution.ApplyTopLevel(element, b, ctx);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
                {
                    // Same collect-all net ParseStep has: a throwing DSL call becomes one
                    // positioned error and the pass continues.
                    ctx.AddError(element, ex.Message);
                }
            }
            foreach (var element in routeElements)
                ParseRoute(element, b, ctx);
        });
        builder.BuildNow(_context);

        ctx.CheckSchemes();
        ValidateAgainstSchema(document, sourceName, ctx);
        if (ctx.Errors.Count > 0)
            throw new XmlRouteException(sourceName, ctx.Errors);

        _context.AddRoutes(builder);
    }

    /// <summary>
    /// A container-level handler (<c>&lt;onException&gt;</c>, the intercepts,
    /// <c>&lt;onCompletion&gt;</c>): declared on the builder for every route of the file, and
    /// configured by the SAME helpers the route-level contributions use — no second copy. A
    /// builder-level definition has no parent, so no <c>End…()</c> is called here.
    /// </summary>
    private static void ParseContainerHandler(XElement element, XmlRoutesBuilder builder, XmlParseContext ctx)
    {
        switch (element.Name.LocalName)
        {
            case "onException":
            {
                var types = CoreElements.CoreContributions.ExceptionTypes(element, ctx);
                if (types.Length == 0) return;
                var handler = builder.OnException(types);
                ctx.ApplyBranchIdentity(element, handler);
                CoreElements.CoreContributions.ConfigureOnException(handler, element, ctx);
                return;
            }
            case "intercept":
            {
                var handler = builder.Intercept();
                ctx.ApplyBranchIdentity(element, handler);
                CoreElements.CoreContributions.ConfigureIntercept(handler, element, ctx);
                return;
            }
            case "interceptFrom":
            {
                var handler = builder.InterceptFrom(ctx.Attr(element, "uri"));
                ctx.ApplyBranchIdentity(element, handler);
                CoreElements.CoreContributions.ConfigureIntercept(handler, element, ctx);
                return;
            }
            case "interceptSendToEndpoint":
            {
                var uri = ctx.RequiredAttr(element, "uri");
                if (uri is null) return;
                var handler = builder.InterceptSendToEndpoint(uri);
                ctx.ApplyBranchIdentity(element, handler);
                CoreElements.CoreContributions.ConfigureIntercept(handler, element, ctx);
                return;
            }
            default:
            {
                var handler = builder.OnCompletion();
                ctx.ApplyBranchIdentity(element, handler);
                CoreElements.CoreContributions.ConfigureOnCompletion(handler, element, ctx);
                return;
            }
        }
    }

    private static void ParseRoute(XElement element, XmlRoutesBuilder builder, XmlParseContext ctx)
    {
        var from = element.Elements().FirstOrDefault(e => e.Name.LocalName == "from");
        if (from is null)
        {
            ctx.AddError(element, "<route> requires exactly one <from>.");
            return;
        }
        if (element.Elements().Count(e => e.Name.LocalName == "from") > 1)
        {
            ctx.AddError(element, "<route> must have exactly one <from>; several found.");
            return;
        }

        var uri = StructuredEndpoint.Resolve(from, ctx, out var position);
        if (uri is null)
            return;
        if (uri.Contains("${", StringComparison.Ordinal))
        {
            // V4: a ${...} placeholder in a consumer URI fails Start() — there is no message to
            // resolve against. The loader says it earlier and with the position.
            ctx.AddError(position, "a consumer URI cannot carry ${...} — only constants and {{key}} " +
                                   "configuration placeholders are valid in <from>.");
            return;
        }
        ctx.NoteEndpointUri(position, uri);

        // enabled= (Ф0 §3, принято 2026-09-02): false after {{...}} resolution — the route is
        // not registered at all. Placeholders resolve through the context's own lookup chain
        // (IConfiguration, then context properties), so per-stand switching needs no code.
        if (ctx.Attr(element, "enabled") is { } enabled)
        {
            string resolved;
            try
            {
                // A test double of IRouteContext has no placeholder chain — the literal is used.
                resolved = ctx.RouteContext is RouteContext live ? live.ResolvePlaceholders(enabled) : enabled;
            }
            catch (InvalidOperationException ex)
            {
                ctx.AddError(element, ex.Message);
                return;
            }
            if (!bool.TryParse(resolved, out var isEnabled))
            {
                ctx.AddError(element, $"enabled=\"{enabled}\" resolved to '{resolved}', which is not a bool.");
                return;
            }
            if (!isEnabled)
                return;
        }

        IRouteDefinition route = builder.From(uri);
        ApplyRouteAttributes(element, route, ctx);
        foreach (var child in element.Elements().Where(c => c.Name.LocalName != "from"))
            route = ctx.ParseStep(child, route);
    }

    private static void ApplyRouteAttributes(XElement element, IRouteDefinition route, XmlParseContext ctx)
    {
        if (ctx.Attr(element, "id") is { Length: > 0 } id)
            route.RouteId(id);
        if (ctx.Attr(element, "description") is { Length: > 0 } description)
            route.Description(description);
        if (ctx.Convert<bool>(element, "autoStart") is { } autoStart)
            route.AutoStart(autoStart);
        if (ctx.Convert<bool>(element, "cluster") is { } cluster)
            route.Cluster(cluster);
        if (ctx.Convert<bool>(element, "messageHistory") is { } history)
            route.MessageHistory(history);
        if (ctx.Convert<TimeSpan>(element, "processingTimeout") is { } timeout)
            route.ProcessingTimeout(timeout);
        if (ctx.Attr(element, "routePolicy") is { Length: > 0 } policy)
            route.RoutePolicy(policy);
    }

    /// <summary>
    /// The builder XML routes live in. The document parse IS its Configure — run eagerly by the
    /// loader so every error surfaces at Load; <see cref="RouteBuilder.IsBuilt"/> then keeps
    /// Start() from re-running it (InternalBuild clears a builder before configuring).
    /// </summary>
    private sealed class XmlRoutesBuilder(Action<XmlRoutesBuilder> parse) : RouteBuilder
    {
        protected override void Configure() => parse(this);
        internal void BuildNow(IRouteContext context) => InternalBuild(context);
    }
}

/// <summary>
/// The <c>&lt;bean&gt;</c> container section: declares an object in the context registry. The
/// markup is read by <see cref="BeanModel"/> and built by <see cref="BeanFactory"/> — the same
/// pair generated code and the package gate use. References resolve against the registry as it
/// stands, so a bean may refer only to beans declared before it (document order, the context
/// file first) or registered by module code; anything else is a positioned error.
/// </summary>
internal static class BeanSection
{
    internal static void Declare(XElement element, XmlParseContext ctx)
    {
        var name = ctx.RequiredAttr(element, "name");
        if (name is null)
            return;
        var declaration = BeanModel.Parse(element, ctx.AddError);
        if (declaration is null)
            return;
        var instance = Build(declaration, ctx);
        if (instance is not null)
            ctx.RouteContext.AddToRegistry(name, instance);
    }

    private static object? Build(BeanDeclaration bean, XmlParseContext ctx)
    {
        var resolver = ctx.RouteContext.GetService<IBeanTypeResolver>() ?? DefaultBeanTypeResolver.Instance;
        var type = resolver.Resolve(bean.TypeName);
        if (type is null)
        {
            ctx.AddError(bean.At, $"<bean> type '{bean.TypeName}' was not found in the loaded assemblies.");
            return null;
        }

        var args = new List<object?>();
        List<Type>? argumentTypes = bean.TypedArguments ? [] : null;
        foreach (var arg in bean.ConstructorArgs)
        {
            if (!TryRaw(arg.Value, ctx, out var raw))
                return null;
            args.Add(raw);
            if (arg.TypeName is null)
                continue;
            if (resolver.Resolve(arg.TypeName) is not { } argumentType)
            {
                ctx.AddError(arg.Value.At, $"<constructorArg type=\"{arg.TypeName}\">: the type was not found in the loaded assemblies.");
                return null;
            }
            argumentTypes!.Add(argumentType);
        }
        var resolve = Placeholders(ctx);
        object instance;
        try
        {
            instance = BeanFactory.Instantiate(
                type, args, argumentTypes, bean.FactoryMethod, ctx.RouteContext.GetServiceProvider() ?? EmptyProvider.Instance, resolve);
        }
        catch (Exception ex)
        {
            ctx.AddError(bean.At, (ex as TargetInvocationException)?.InnerException?.Message ?? ex.Message);
            return null;
        }

        foreach (var property in bean.Properties)
        {
            if (!TryRaw(property.Value, ctx, out var raw))
                continue;
            try
            {
                BeanFactory.Assign(instance, property.Key, raw, resolve);
            }
            catch (Exception ex)
            {
                ctx.AddError(property.At, (ex as TargetInvocationException)?.InnerException?.Message ?? ex.Message);
            }
        }
        return instance;
    }

    /// <summary>
    /// The raw form <see cref="BeanFactory"/> binds: text as it is written, a reference as the
    /// registered object, a nested bean as its instance, a list as its raw items. False after
    /// recording an error at the element that caused it.
    /// </summary>
    private static bool TryRaw(BeanValue value, XmlParseContext ctx, out object? raw)
    {
        raw = null;
        switch (value)
        {
            case ScalarBeanValue scalar:
                raw = scalar.Text;
                return true;
            case RefBeanValue reference:
                raw = ctx.RouteContext.GetFromRegistry<object>(reference.Bean);
                if (raw is null)
                    ctx.AddError(reference.At,
                        $"bean reference '{reference.Bean}' names nothing registered so far — beans are built in " +
                        "document order (the context file first), so declare it above this one, or register it from module code before the XML loads.");
                return raw is not null;
            case NestedBeanValue nested:
                raw = Build(nested.Bean, ctx);
                return raw is not null;
            case ListBeanValue list:
            {
                Type? elementType = null;
                if (list.ElementTypeName is not null)
                {
                    var resolver = ctx.RouteContext.GetService<IBeanTypeResolver>() ?? DefaultBeanTypeResolver.Instance;
                    elementType = resolver.Resolve(list.ElementTypeName);
                    if (elementType is null)
                    {
                        ctx.AddError(list.At, $"<list of=\"{list.ElementTypeName}\">: the type was not found in the loaded assemblies.");
                        return false;
                    }
                }
                var items = new List<object?>(list.Items.Count);
                var complete = true;
                foreach (var item in list.Items)
                {
                    complete &= TryRaw(item, ctx, out var itemRaw);
                    items.Add(itemRaw);
                }
                raw = new BeanListValue(elementType, items);
                return complete;
            }
            default:
                throw new InvalidOperationException($"unhandled bean value {value.GetType().Name}.");
        }
    }

    /// <summary>
    /// Bean values may carry <c>{{key}}</c> / <c>{{key:default}}</c> — resolved through the
    /// context's own placeholder chain before type conversion; an unresolved key without a
    /// default throws, and the caller positions it at the slot.
    /// </summary>
    private static Func<string, string> Placeholders(XmlParseContext ctx)
        => ctx.RouteContext is RouteContext live
            ? text => text.Contains("{{", StringComparison.Ordinal) ? live.ResolvePlaceholders(text) : text
            : text => text;

    private sealed class EmptyProvider : IServiceProvider
    {
        public static readonly EmptyProvider Instance = new();
        public object? GetService(Type serviceType) => null;
    }
}

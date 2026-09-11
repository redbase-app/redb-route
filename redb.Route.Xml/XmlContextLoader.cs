using System.Xml.Linq;
using Microsoft.Extensions.DependencyInjection;
using redb.Route.Abstractions;
using redb.Route.Components.Bean;
using redb.Route.Core;
using redb.Route.Definitions;

namespace redb.Route.Xml;

/// <summary>
/// Loads a <c>context.xml</c> document (Ф0 эскиз 11, принят владельцем): the context-level
/// sections a package carries besides its routes — an explicit <c>&lt;components&gt;</c> list,
/// context-wide <c>&lt;bean&gt;</c> declarations, and the <c>&lt;onInit&gt;</c> pipeline of
/// ordinary format steps, run ONCE at <see cref="IRouteLifecycleListener.OnContextStarting"/> —
/// the engine's only fail-fast bootstrap hook, so a failed step keeps the context from
/// accepting traffic, exactly the module-init semantics the sketch asks for. Same eager
/// collect-all parsing with positions as the routes loader.
/// </summary>
public sealed class XmlContextLoader
{
    private readonly RouteContext _context;
    private readonly ElementRegistry _registry;
    private readonly bool _validate;
    private readonly bool _hasExtensions;

    /// <summary>Creates a loader for the given context.</summary>
    public XmlContextLoader(RouteContext context, XmlRouteLoaderOptions? options = null)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _registry = ElementRegistry.CreateDefault(options?.Extensions);
        _validate = options?.ValidateAgainstSchema ?? true;
        _hasExtensions = options?.Extensions is { Count: > 0 };
    }

    /// <summary>Parses and applies every section of the document. See the class remarks.</summary>
    public void Load(XDocument document, string? sourceName = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        sourceName ??= "<inline>";
        var ctx = new XmlParseContext(_context, _registry, sourceName);

        var root = document.Root;
        if (root is null || root.Name.LocalName != "context")
        {
            throw new XmlRouteException(sourceName,
                [$"the document root must be <context xmlns=\"{XmlRouteLoader.Namespace}\">, found <{root?.Name.LocalName ?? "nothing"}>."]);
        }
        if (root.Name.NamespaceName != XmlRouteLoader.Namespace)
        {
            throw new XmlRouteException(sourceName,
                [XmlRouteLoader.VersionError(root.Name.NamespaceName)
                 ?? $"unexpected root namespace '{root.Name.NamespaceName}'; this loader reads '{XmlRouteLoader.Namespace}'."]);
        }
        ctx.CheckElementNamespaces(root);

        // Components first, then beans (a bean may need a component's types loaded), then init.
        foreach (var components in root.Elements().Where(e => e.Name.LocalName == "components"))
            RegisterComponents(components, ctx);
        foreach (var bean in root.Elements().Where(e => e.Name.LocalName == "bean"))
            BeanSection.Declare(bean, ctx);

        var onInits = root.Elements().Where(e => e.Name.LocalName == "onInit").ToList();
        if (onInits.Count > 1)
            ctx.AddError(onInits[1], "<context> takes at most one <onInit>.");

        // Context-level contributions (Р21): a package element beside <components>/<bean>/<onInit>
        // (the redb bridge above all) applies to the context AFTER beans — a contribution may
        // reference #names — and BEFORE <onInit> parsing, whose steps may rely on what the
        // contribution set up.
        foreach (var element in root.Elements())
        {
            if (element.Name.LocalName is "components" or "bean" or "onInit")
                continue;
            if (ctx.Registry.Find(element.Name.LocalName) is IXmlContextContribution contextLevel
                && contextLevel.Kind == XmlElementKind.ContextLevel)
            {
                contextLevel.ApplyContext(element, _context, ctx);
                continue;
            }
            var contributed = ctx.Registry.Contributions
                .Where(c => c.Kind == XmlElementKind.ContextLevel)
                .Select(c => "<" + c.Name + ">");
            ctx.AddError(element, $"unknown <context> section <{element.Name.LocalName}> " +
                                  $"(valid: <components>, <bean>, <onInit>{string.Concat(contributed.Select(n => ", " + n))}).");
        }

        RouteDefinition? init = null;
        if (onInits.Count == 1)
        {
            // Ordinary steps of the format, parsed by the same registry; <from> has no place in
            // an init pipeline and is rejected as an unknown element naturally.
            init = new RouteDefinition();
            ctx.ParseSteps(onInits[0], init);
        }

        ctx.CheckSchemes();
        if (_validate)
        {
            var schema = _hasExtensions ? XmlRouteSchema.For(_registry) : XmlRouteSchema.CoreSet;
            foreach (var (line, column, message) in XmlRouteSchema.Validate(document, schema))
            {
                if (!ctx.ErrorLines.Contains(line))
                    ctx.AddRawError($"{sourceName}({line},{column}): [schema] {message}");
            }
        }
        if (ctx.Errors.Count > 0)
            throw new XmlRouteException(sourceName, ctx.Errors);

        if (init is not null && init.Outputs.Count > 0)
            _context.AddLifecycleListener(new XmlInitListener(init, sourceName));
    }

    private void RegisterComponents(XElement section, XmlParseContext ctx)
    {
        foreach (var child in section.Elements())
        {
            if (child.Name.LocalName != "component")
            {
                ctx.AddError(child, $"<components> accepts only <component type=…/> children, found <{child.Name.LocalName}>.");
                continue;
            }
            var typeName = ctx.RequiredAttr(child, "type");
            if (typeName is null)
                continue;
            var resolver = _context.GetService<IBeanTypeResolver>() ?? DefaultBeanTypeResolver.Instance;
            var type = resolver.Resolve(typeName);
            if (type is null)
            {
                ctx.AddError(child, $"component type '{typeName}' was not found in the loaded assemblies.");
                continue;
            }
            if (!typeof(IComponent).IsAssignableFrom(type))
            {
                ctx.AddError(child, $"'{typeName}' does not implement IComponent.");
                continue;
            }
            try
            {
                var provider = _context.GetServiceProvider();
                var component = (IComponent)(provider is not null
                    ? ActivatorUtilities.CreateInstance(provider, type)
                    : Activator.CreateInstance(type)
                      ?? throw new InvalidOperationException($"Activator returned null for {type}."));
                _context.AddComponent(component);
            }
            catch (Exception ex) when (ex is MissingMethodException or MemberAccessException or InvalidOperationException)
            {
                ctx.AddError(child, $"component '{typeName}' could not be created: {ex.Message}");
            }
        }
    }
}

/// <summary>
/// Runs the parsed <c>&lt;onInit&gt;</c> pipeline once, in the fail-fast bootstrap phase. The
/// steps compile through the ordinary node pipeline (so telemetry decoration and disposal
/// registration apply) with the live context, and a step failure aborts <c>Start()</c>.
/// </summary>
internal sealed class XmlInitListener(RouteDefinition pipeline, string sourceName) : IRouteLifecycleListener
{
    private bool _ran; // set only on success: a failed Start() must retry the init on the next Start()

    public async Task OnContextStarting(IRouteContext context, CancellationToken ct)
    {
        if (_ran)
            return;
        var body = NodePipeline.Body(context, pipeline.Outputs);
        var exchange = new Exchange(new Message((object?)null));
        try
        {
            await body.Process(exchange, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception thrown)
        {
            // A step may throw directly or record on the exchange — both name the source file.
            throw new InvalidOperationException($"<onInit> of '{sourceName}' failed: {thrown.Message}", thrown);
        }
        if (exchange.Exception is { } failure && !exchange.ExceptionHandled)
            throw new InvalidOperationException($"<onInit> of '{sourceName}' failed: {failure.Message}", failure);
        _ran = true;
    }
}

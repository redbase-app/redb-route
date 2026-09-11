using System.Xml.Linq;
using redb.Route.Abstractions;
using redb.Route.Expressions;

namespace redb.Route.Xml.CoreElements;

/// <summary>
/// The core element set — every contribution is a facade over one DSL verb (Р1). This part
/// holds the first vertical slice; the rest of the Ф0 catalog lives in
/// <c>CoreContributions.Catalog.cs</c>, all through the same registry.
/// </summary>
internal static partial class CoreContributions
{
    internal static IReadOnlyList<IXmlElementContribution> All { get; } = [.. Basic(), .. Catalog()];

    private static IXmlElementContribution[] Basic() =>
    [
        // ── leaves ──────────────────────────────────────────────────────────
        Step("to", (e, cur, ctx) =>
        {
            var uri = StructuredEndpoint.Resolve(e, ctx, out var position);
            if (uri is null) return cur;
            ctx.NoteEndpointUri(position, uri);
            return cur.To(uri);
        }),
        Step("toD", (e, cur, ctx) =>
        {
            var uri = StructuredEndpoint.Resolve(e, ctx, out _);
            return uri is null ? cur : cur.ToD(uri);
        }),
        Step("setHeader", (e, cur, ctx) =>
        {
            var name = ctx.RequiredAttr(e, "name");
            var pick = ctx.ExactlyOneOf(e, "value", "expr");
            if (name is null || pick is null) return cur;
            return pick.Value.Name == "value"
                ? cur.SetHeader(name, pick.Value.Value)
                : cur.SetHeader(name, new StringExpression(pick.Value.Value));
        }),
        Step("setProperty", (e, cur, ctx) =>
        {
            var name = ctx.RequiredAttr(e, "name");
            var pick = ctx.ExactlyOneOf(e, "value", "expr");
            if (name is null || pick is null) return cur;
            return pick.Value.Name == "value"
                ? cur.SetProperty(name, pick.Value.Value)
                : cur.SetProperty(name, new StringExpression(pick.Value.Value));
        }),
        Step("setBody", (e, cur, ctx) =>
        {
            var pick = ctx.ExactlyOneOf(e, "value", "expr");
            if (pick is null) return cur;
            return pick.Value.Name == "value"
                ? cur.SetBody(pick.Value.Value)
                : cur.SetBody(new StringExpression(pick.Value.Value));
        }),
        Step("transform", (e, cur, ctx) =>
        {
            var expr = ctx.RequiredAttr(e, "expr");
            return expr is null ? cur : cur.Transform(new StringExpression(expr));
        }),
        Step("removeHeader", (e, cur, ctx) =>
        {
            var name = ctx.RequiredAttr(e, "name");
            return name is null ? cur : cur.RemoveHeader(name);
        }),
        Step("removeProperty", (e, cur, ctx) =>
        {
            var name = ctx.RequiredAttr(e, "name");
            return name is null ? cur : cur.RemoveProperty(name);
        }),
        Step("removeBody", (e, cur, ctx) => cur.RemoveBody()),
        Step("removeHeaders", (e, cur, ctx) =>
        {
            var pattern = ctx.RequiredAttr(e, "pattern");
            if (pattern is null) return cur;
            var except = SplitList(ctx.Attr(e, "except"));
            return cur.RemoveHeaders(pattern, except);
        }),
        Step("removeProperties", (e, cur, ctx) =>
        {
            var pattern = ctx.RequiredAttr(e, "pattern");
            if (pattern is null) return cur;
            var except = SplitList(ctx.Attr(e, "except"));
            return cur.RemoveProperties(pattern, except);
        }),
        Step("log", (e, cur, ctx) =>
        {
            // Р5.1: children mean the rich scope, plain text means the simple form; both — error.
            var text = string.Concat(e.Nodes().OfType<XText>().Select(t => t.Value));
            var children = e.Elements().ToList();
            if (children.Count > 0 && !string.IsNullOrWhiteSpace(text))
            {
                ctx.AddError(e, "<log> carries either message text or child elements, not both.");
                return cur;
            }
            var level = Microsoft.Extensions.Logging.LogLevel.Information;
            if (ctx.Attr(e, "level") is { } levelName &&
                !Enum.TryParse(levelName, ignoreCase: true, out level))
            {
                ctx.AddError(e, $"'{levelName}' is not a log level (Trace, Debug, Information, Warning, Error, Critical).");
                return cur;
            }
            if (children.Count == 0)
            {
                if (string.IsNullOrWhiteSpace(text))
                {
                    ctx.AddError(e, "<log> needs message text or <message>/<header>/<property> children.");
                    return cur;
                }
                return cur.Log(text.Trim(), level);
            }
            var rich = cur.Log(level);
            if (ctx.Convert<bool>(e, "showRouteId") == true)
                rich.ShowRouteId();
            foreach (var child in children)
            {
                switch (child.Name.LocalName)
                {
                    case "message":
                        rich.Message(child.Value);
                        break;
                    case "header":
                        if (ctx.RequiredAttr(child, "name") is { } header) rich.Header(header);
                        break;
                    case "property":
                        if (ctx.RequiredAttr(child, "name") is { } property) rich.Property(property);
                        break;
                    default:
                        ctx.AddError(child, $"<log> accepts <message>, <header> and <property> children, found <{child.Name.LocalName}>.");
                        break;
                }
            }
            return rich.EndLog();
        }),
        Step("delay", (e, cur, ctx) =>
        {
            var pick = ctx.ExactlyOneOf(e, "duration", "expr");
            if (pick is null) return cur;
            if (pick.Value.Name == "expr")
                return cur.Delay(pick.Value.Value);
            if (TimeSpan.TryParse(pick.Value.Value, System.Globalization.CultureInfo.InvariantCulture, out var duration))
                return cur.Delay(duration);
            ctx.AddError(e, $"'{pick.Value.Value}' is not a TimeSpan (use hh:mm:ss or hh:mm:ss.fff).");
            return cur;
        }),
        Step("stop", (e, cur, ctx) => cur.Stop()),
        Step("throwException", (e, cur, ctx) =>
        {
            var typeName = ctx.RequiredAttr(e, "type");
            if (typeName is null) return cur;
            var type = ResolveType(typeName, e, ctx);
            if (type is null) return cur;
            if (!typeof(Exception).IsAssignableFrom(type))
            {
                ctx.AddError(e, $"'{typeName}' is not an Exception type.");
                return cur;
            }
            return cur.ThrowException(type, ctx.Attr(e, "message") ?? "error");
        }),
        Step("convertBody", (e, cur, ctx) =>
        {
            var typeName = ctx.RequiredAttr(e, "type");
            if (typeName is null) return cur;
            var type = ResolveType(typeName, e, ctx);
            return type is null ? cur : cur.ConvertBody(type);
        }),
        Step("wireTap", (e, cur, ctx) =>
        {
            var uri = StructuredEndpoint.Resolve(e, ctx, out var position);
            if (uri is null) return cur;
            ctx.NoteEndpointUri(position, uri);
            return cur.WireTap(uri);
        }),
        Step("validate", (e, cur, ctx) =>
        {
            var expr = ctx.RequiredAttr(e, "expr");
            if (expr is null) return cur;
            var throwOnFailure = ctx.Convert<bool>(e, "throwOnFailure") ?? true;
            return cur.Validate(expr, ctx.Attr(e, "message") ?? "Validation failed", throwOnFailure);
        }),

        // ── scopes ──────────────────────────────────────────────────────────
        new DelegateContribution("filter", XmlElementKind.Scope, (e, cur, ctx) =>
        {
            var pick = ctx.ExactlyOneOf(e, "expr", "predicate");
            if (pick is null) return cur;
            var scope = pick.Value.Name == "expr"
                ? cur.Filter(pick.Value.Value)
                : cur.Filter(ResolvePredicate(pick.Value.Value, e, ctx) ?? AlwaysFalse.Instance);
            ctx.ParseSteps(e, scope);
            return scope.EndFilter();
        }),
        new DelegateContribution("split", XmlElementKind.Scope, (e, cur, ctx) =>
        {
            // Ф0 §4.3: the source is the expr attribute or exactly one tokenizer child.
            var expr = ctx.Attr(e, "expr");
            var tokenizers = e.Elements().Where(c => c.Name.LocalName
                is "tokenizeLines" or "tokenizeXml" or "tokenizeJsonArray").ToList();
            if ((expr is null) == (tokenizers.Count == 0) || tokenizers.Count > 1)
            {
                ctx.AddError(e, "<split> takes either expr or exactly one tokenizer child " +
                                "(<tokenizeLines>, <tokenizeXml>, <tokenizeJsonArray>).");
                return cur;
            }
            var scope = expr is not null
                ? cur.Split(new StringExpression(expr))
                : cur.Split(Tokenizer(tokenizers[0], ctx));
            if (ctx.Convert<bool>(e, "parallel") == true) scope.ParallelProcessing();
            if (ctx.Convert<int>(e, "maxParallelism") is { } dop) scope.MaxParallelism(dop);
            if (ctx.Convert<bool>(e, "stopOnException") == true) scope.StopOnException();
            IRouteDefinition inner = scope;
            foreach (var child in e.Elements().Except(tokenizers))
                inner = ctx.ParseStep(child, inner);
            return scope.EndSplit();
        }),

        // ── branching ───────────────────────────────────────────────────────
        new DelegateContribution("choice", XmlElementKind.Branching, (e, cur, ctx) =>
        {
            var choice = cur.Choice();
            foreach (var branch in e.Elements())
            {
                switch (branch.Name.LocalName)
                {
                    case "when":
                    {
                        var pick = ctx.ExactlyOneOf(branch, "expr", "predicate");
                        if (pick is null) continue;
                        var when = pick.Value.Name == "expr"
                            ? choice.When(pick.Value.Value)
                            : choice.When(ResolvePredicate(pick.Value.Value, branch, ctx) ?? AlwaysFalse.Instance);
                        ctx.ApplyBranchIdentity(branch, when);
                        ctx.ParseSteps(branch, when);
                        break;
                    }
                    case "otherwise":
                    {
                        var otherwise = choice.Otherwise();
                        ctx.ApplyBranchIdentity(branch, otherwise);
                        ctx.ParseSteps(branch, otherwise);
                        break;
                    }
                    default:
                        ctx.AddError(branch, $"<choice> accepts only <when> and <otherwise>, found <{branch.Name.LocalName}>.");
                        break;
                }
            }
            return choice.EndChoice();
        }),
    ];

    // ── helpers ─────────────────────────────────────────────────────────────

    private static DelegateContribution Step(
        string name, Func<XElement, IRouteDefinition, XmlParseContext, IRouteDefinition> apply)
        => new(name, XmlElementKind.Step, apply);

    private static Func<IExchange, IAsyncEnumerable<object?>> Tokenizer(XElement t, XmlParseContext ctx)
    {
        switch (t.Name.LocalName)
        {
            case "tokenizeLines":
            {
                var separator = ctx.Attr(t, "separator") ?? "\n";
                var skipEmpty = ctx.Convert<bool>(t, "skipEmpty") ?? false;
                return exchange => redb.Route.Expressions.Tokenizers.LineTokenizer
                    .Tokenize(exchange.In.Body, separator, skipEmpty);
            }
            case "tokenizeXml":
            {
                var elementName = ctx.RequiredAttr(t, "element") ?? "";
                var inheritFrom = ctx.Attr(t, "inheritNamespaceFrom");
                return exchange => redb.Route.Expressions.Tokenizers.XmlTokenizer
                    .Tokenize(exchange.In.Body, elementName, inheritFrom);
            }
            default:
                return exchange => redb.Route.Expressions.Tokenizers.JsonArrayTokenizer
                    .Tokenize(exchange.In.Body);
        }
    }

    private static string[] SplitList(string? csv)
        => string.IsNullOrWhiteSpace(csv)
            ? []
            : csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static Type? ResolveType(string typeName, XElement e, XmlParseContext ctx)
    {
        var resolver = ctx.RouteContext.GetService<IBeanTypeResolver>()
            ?? redb.Route.Components.Bean.DefaultBeanTypeResolver.Instance;
        var type = resolver.Resolve(typeName);
        if (type is null)
            ctx.AddError(e, $"type '{typeName}' was not found in the loaded assemblies.");
        return type;
    }

    private static IPredicate? ResolvePredicate(string reference, XElement e, XmlParseContext ctx)
    {
        var predicate = ctx.RouteContext.GetFromRegistry<IPredicate>(reference);
        if (predicate is null)
            ctx.AddError(e, $"predicate '{reference}' is not in the context registry (expected an IPredicate).");
        return predicate;
    }

    /// <summary>Substitute for a missing registry predicate, so the pass can continue collecting errors.</summary>
    private sealed class AlwaysFalse : IPredicate
    {
        public static readonly AlwaysFalse Instance = new();
        public bool Matches(IExchange exchange) => false;
    }

    /// <summary>A contribution defined by a delegate — the shape every core element uses.</summary>
    internal sealed class DelegateContribution(
        string name, XmlElementKind kind,
        Func<XElement, IRouteDefinition, XmlParseContext, IRouteDefinition> apply) : IXmlElementContribution
    {
        public string Name { get; } = name;
        public XmlElementKind Kind { get; } = kind;
        public ElementSpec Spec => SpecFor(Name, Kind);
        public IRouteDefinition Apply(XElement element, IRouteDefinition current, XmlParseContext context)
            => apply(element, current, context);
        public void Print(XElement element, XmlCodeWriter code)
        {
            if (!Printers.TryGetValue(Name, out var printer))
                throw new NotSupportedException($"core element <{Name}> has no printer — the Ф6 face is missing.");
            printer(element, code);
        }
    }
}

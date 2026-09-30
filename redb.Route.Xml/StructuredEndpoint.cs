using System.Text;
using System.Xml.Linq;
using redb.Route.Abstractions;

namespace redb.Route.Xml;

/// <summary>
/// The structured endpoint form (Ф0 §7.2): an address-carrying step (<c>&lt;from&gt;</c>,
/// <c>&lt;to&gt;</c>, <c>&lt;toD&gt;</c>, <c>&lt;wireTap&gt;</c>, <c>&lt;enrich&gt;</c>,
/// <c>&lt;pollEnrich&gt;</c>) takes either <c>uri=</c> or exactly one child endpoint element,
/// normalized HERE into the same URI string a hand-written address would be — the engine
/// (endpoint cache, statistics, mock masks, secret redaction) sees one canon and zero new code.
/// One generic procedure for every scheme: no switch per connector, ever (owner rule
/// 2026-09-02 — «общий подход, копипаст запрещён конструкцией»).
/// </summary>
internal static class StructuredEndpoint
{
    /// <summary>
    /// The endpoint URI of an address-carrying step, whichever form it uses. Returns null after
    /// recording errors; <paramref name="position"/> is the element to blame in messages (the
    /// endpoint child for the structured form, the step itself otherwise).
    /// </summary>
    internal static string? Resolve(XElement step, XmlParseContext ctx, out XElement position)
    {
        position = step;
        var uri = ctx.Attr(step, "uri");
        var children = step.Elements().ToList();
        if (uri is not null)
        {
            if (children.Count > 0)
            {
                ctx.AddError(step, $"<{step.Name.LocalName}> takes either uri= or a structured endpoint child, not both.");
                return null;
            }
            return uri;
        }
        if (children.Count == 0)
        {
            ctx.AddError(step, $"<{step.Name.LocalName}> requires the uri attribute or one endpoint child " +
                               "(the structured form, e.g. <sql dataSource=\"#main-db\">…</sql>).");
            return null;
        }
        if (children.Count > 1)
        {
            ctx.AddError(step, $"<{step.Name.LocalName}> takes exactly one endpoint child; {children.Count} found.");
            return null;
        }
        position = children[0];
        return Build(children[0], ctx);
    }

    /// <summary>Builds <c>scheme://path?options</c> from one endpoint element. See the class remarks.</summary>
    private static string? Build(XElement endpoint, XmlParseContext ctx)
    {
        var scheme = endpoint.Name.LocalName;

        // The path part: the universal path= attribute, the component's own one-line synonym
        // (kafka → topic; read from the component, never from a list here — Ф4), or the
        // element's text content (CDATA — the SQL-style connectors whose path IS a text).
        var component = (ctx.RouteContext as redb.Route.Core.RouteContext)
            ?.GetComponent<IComponent>(scheme) as redb.Route.Core.ComponentBase;
        var synonym = component?.StructuredPathSynonym is { Length: > 0 } s ? s : null;
        var pathAttr = ctx.Attr(endpoint, "path");
        var synonymAttr = synonym is not null ? ctx.Attr(endpoint, synonym) : null;
        var text = Text(endpoint);
        if (component?.PathIsText == true && (pathAttr is not null || synonymAttr is not null))
        {
            ctx.AddError(endpoint, $"the path of <{scheme}> is a text — put it in the element content (CDATA), not an attribute.");
            return null;
        }
        var pathSources = new[] { pathAttr, synonymAttr, text }.Count(v => v is not null);
        if (pathSources > 1)
        {
            ctx.AddError(endpoint, $"<{scheme}> carries its path more than once " +
                                   $"(path=, {(synonym is null ? "" : $"{synonym}=, ")}text content — exactly one).");
            return null;
        }
        var path = pathAttr ?? synonymAttr ?? text ?? string.Empty;
        if (path.Contains('?'))
        {
            ctx.AddError(endpoint, $"the path of <{scheme}> contains '?', which starts the option part of a URI — " +
                                   "this address is not expressible as an endpoint URI.");
            return null;
        }

        var options = Options(endpoint, synonym, ctx.AddError);
        if (options is null)
            return null;

        var builder = new StringBuilder(scheme).Append("://").Append(path);
        var separator = '?';
        foreach (var (key, value, _) in options)
        {
            builder.Append(separator).Append(key).Append('=').Append(EscapeValue(value));
            separator = '&';
        }
        return builder.ToString();
    }

    /// <summary>
    /// The query options one endpoint element writes, in document order, each with the element
    /// that wrote it — what <see cref="Build"/> puts after '?', and what the package gate holds
    /// against the connector's options. Null after reporting a malformed child.
    /// <list type="bullet">
    /// <item>Every unqualified attribute is an option verbatim — the same names, the same
    /// converter, the same unknown-name rule the URI form has (typed validation happens where it
    /// always did: in <c>BindFromUri</c> at endpoint creation). <c>path=</c> and the component's
    /// path synonym are the path, not options.</item>
    /// <item>An attribute in a foreign namespace is metadata for other tools (Р9: tolerated,
    /// never read) — it is not an option, or a strict connector would refuse an editor's mark.</item>
    /// <item>Two generic child forms, both an orthography of URI options (Ф0 §7.2 п.6): a family
    /// entry <c>&lt;param name="login" value="…"/&gt;</c> → <c>param.login=…</c>; a long text
    /// option <c>&lt;onSuccess&gt;&lt;![CDATA[…]]&gt;&lt;/onSuccess&gt;</c> → <c>onSuccess=…</c>.</item>
    /// </list>
    /// </summary>
    internal static List<(string Key, string Value, XElement At)>? Options(
        XElement endpoint, string? pathSynonym, Action<XElement, string> error)
    {
        var options = new List<(string Key, string Value, XElement At)>();
        foreach (var attribute in endpoint.Attributes())
        {
            if (attribute.IsNamespaceDeclaration || attribute.Name.NamespaceName.Length > 0
                || attribute.Name.LocalName == "path"
                || (pathSynonym is not null && attribute.Name.LocalName == pathSynonym))
                continue;
            options.Add((attribute.Name.LocalName, attribute.Value, endpoint));
        }

        foreach (var child in endpoint.Elements())
        {
            var name = child.Attribute("name")?.Value;
            var value = child.Attribute("value")?.Value;
            var content = Text(child);
            if (name is not null && value is not null && content is null && !child.Elements().Any())
            {
                options.Add(($"{child.Name.LocalName}.{name}", value, child));
                continue;
            }
            if (name is null && content is not null && !child.HasAttributes && !child.Elements().Any())
            {
                options.Add((child.Name.LocalName, content, child));
                continue;
            }
            error(child, $"<{child.Name.LocalName}> inside <{endpoint.Name.LocalName}> must be a family entry " +
                         $"(<{child.Name.LocalName} name=… value=…/>) or a text option " +
                         $"(<{child.Name.LocalName}>content</{child.Name.LocalName}>).");
            return null;
        }
        return options;
    }

    /// <summary>
    /// Option values travel raw, like hand-written URIs and the fluent builders — except the two
    /// characters the engine's query parse cannot survive: '&amp;' splits pairs and '%' is
    /// unescaped by <c>EndpointUriParser</c>. Encoding just those keeps ordinary values
    /// byte-identical to the URI form and round-trips the rest exactly.
    /// </summary>
    private static string EscapeValue(string value)
        => value.Replace("%", "%25").Replace("&", "%26");

    /// <summary>
    /// Context-free normalization for the Ф6 generator: no component lookup, so path synonyms
    /// are unavailable (the tool passes a catalog for those); the universal path=/content and
    /// the two child forms normalize identically to <see cref="Build"/>.
    /// </summary>
    internal static string NormalizeForPrint(XElement endpoint)
    {
        var scheme = endpoint.Name.LocalName;
        var path = endpoint.Attribute("path")?.Value ?? Text(endpoint) ?? string.Empty;
        var options = Options(endpoint, pathSynonym: null, (at, message) =>
            throw new InvalidOperationException($"<{at.Name.LocalName}>: {message}"))!;
        var builder = new StringBuilder(scheme).Append("://").Append(path);
        var separator = '?';
        foreach (var (key, value, _) in options)
        {
            builder.Append(separator).Append(key).Append('=').Append(EscapeValue(value));
            separator = '&';
        }
        return builder.ToString();
    }

    /// <summary>The element's own text (CDATA included), null when blank.</summary>
    private static string? Text(XElement element)
    {
        var text = string.Concat(element.Nodes().OfType<XText>().Select(t => t.Value));
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }
}

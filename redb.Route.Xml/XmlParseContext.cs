using System.Xml;
using System.Xml.Linq;
using redb.Route.Abstractions;
using redb.Route.Core;

namespace redb.Route.Xml;

/// <summary>
/// The traversal state one document is parsed with: the registry, the error collector (every
/// problem of the document in one pass, with positions), the value converter shared with the
/// endpoint options, and the recursion helper scopes call for their children.
/// </summary>
public sealed class XmlParseContext
{
    private readonly List<string> _errors = [];
    private readonly List<(XElement Element, string Uri)> _endpointUris = [];
    private readonly string _sourceName;

    internal XmlParseContext(IRouteContext routeContext, ElementRegistry registry, string sourceName)
    {
        RouteContext = routeContext;
        Registry = registry;
        _sourceName = sourceName;
    }

    /// <summary>The context routes are being loaded into (registry lookups, component names).</summary>
    public IRouteContext RouteContext { get; }

    /// <summary>The element registry the traversal dispatches through.</summary>
    public ElementRegistry Registry { get; }

    /// <summary>All collected errors, each with its file position.</summary>
    public IReadOnlyList<string> Errors => _errors;

    /// <summary>Lines that already carry a parse error — schema validation skips them (§3.4 dedup).</summary>
    internal IReadOnlySet<int> ErrorLines => _errorLines;
    private readonly HashSet<int> _errorLines = [];

    // ── Errors and positions ─────────────────────────────────────────────────

    /// <summary>Records a problem at the element's position; parsing continues.</summary>
    public void AddError(XElement element, string message)
    {
        if (element is IXmlLineInfo { } info && info.HasLineInfo())
            _errorLines.Add(info.LineNumber);
        _errors.Add($"{Position(element)}: {message}");
    }

    /// <summary>Appends an already-positioned message (the schema validation path).</summary>
    internal void AddRawError(string positionedMessage) => _errors.Add(positionedMessage);

    /// <summary>The <c>file(line,column)</c> prefix of an element.</summary>
    public string Position(XElement element)
        => element is IXmlLineInfo info && info.HasLineInfo()
            ? $"{_sourceName}({info.LineNumber},{info.LinePosition})"
            : _sourceName;

    // ── Attributes ───────────────────────────────────────────────────────────

    /// <summary>The attribute's value, or null when absent. Attributes of foreign namespaces are ignored.</summary>
    public string? Attr(XElement element, string name)
        => element.Attribute(name)?.Value;

    /// <summary>
    /// The attribute's value; records an error and returns null when absent or blank — the
    /// contribution then returns the current position unchanged and the pass continues.
    /// </summary>
    public string? RequiredAttr(XElement element, string name)
    {
        var value = element.Attribute(name)?.Value;
        if (string.IsNullOrWhiteSpace(value))
        {
            AddError(element, $"<{element.Name.LocalName}> requires the '{name}' attribute.");
            return null;
        }
        return value;
    }

    /// <summary>
    /// Converts an attribute through the same converter the endpoint options use (invariant
    /// culture; enums by name). A malformed value records an error and returns null.
    /// </summary>
    public T? Convert<T>(XElement element, string name) where T : struct
    {
        var raw = Attr(element, name);
        if (raw is null)
            return null;
        if (OptionValueConverter.Convert(raw, typeof(T)) is T converted)
            return converted;
        AddError(element, $"'{raw}' is not a valid {typeof(T).Name} for the '{name}' attribute.");
        return null;
    }

    /// <summary>
    /// Enforces the exactly-one-of rule for a pair of attributes (Ф0 §5: <c>value</c> vs
    /// <c>expr</c>, <c>expr</c> vs <c>predicate</c>). Returns which one is present, or null after
    /// recording the error.
    /// </summary>
    public (string Name, string Value)? ExactlyOneOf(XElement element, string first, string second)
    {
        var a = Attr(element, first);
        var b = Attr(element, second);
        if (a is not null && b is not null)
        {
            AddError(element, $"<{element.Name.LocalName}> takes '{first}' or '{second}', not both.");
            return null;
        }
        if (a is null && b is null)
        {
            AddError(element, $"<{element.Name.LocalName}> requires '{first}' or '{second}'.");
            return null;
        }
        return a is not null ? (first, a) : (second, b!);
    }

    // ── Traversal ────────────────────────────────────────────────────────────

    /// <summary>
    /// Parses the child elements of <paramref name="parent"/> as a step sequence into
    /// <paramref name="current"/>. Unknown elements and throwing contributions are recorded as
    /// errors and skipped, so one pass reports everything. The generic <c>id</c> /
    /// <c>description</c> attributes (Р12) are applied after each step.
    /// </summary>
    public IRouteDefinition ParseSteps(XElement parent, IRouteDefinition current)
    {
        foreach (var element in parent.Elements())
            current = ParseStep(element, current);
        return current;
    }

    /// <summary>Parses one element as a step (see <see cref="ParseSteps"/>).</summary>
    public IRouteDefinition ParseStep(XElement element, IRouteDefinition current)
    {
        var name = element.Name.LocalName;
        var contribution = Registry.Find(name);
        if (contribution is null)
        {
            var hint = Registry.ClosestTo(name);
            AddError(element, hint is null
                ? $"unknown element <{name}>."
                : $"unknown element <{name}>. Did you mean <{hint}>?");
            return current;
        }

        try
        {
            var next = contribution.Apply(element, current, this);
            ApplyIdentity(element, next);
            return next;
        }
        catch (Exception ex)
        {
            // Collect-all discipline: a throwing DSL call (a malformed expression, a bad enum)
            // becomes one positioned error and the pass continues with the next sibling. The
            // aggregate is thrown at the end of Load, so nothing is swallowed.
            AddError(element, ex.Message);
            return current;
        }
    }

    private static void ApplyIdentity(XElement element, IRouteDefinition current)
    {
        if (element.Attribute("id")?.Value is { Length: > 0 } id)
            current.Id(id);
        if (element.Attribute("description")?.Value is { Length: > 0 } description)
            current.Description(description);
    }

    /// <summary>
    /// Identity for a branch child (<c>&lt;when&gt;</c>, <c>&lt;otherwise&gt;</c>,
    /// <c>&lt;catch&gt;</c>, …) — parsed inside its parent's contribution, so the regular
    /// <c>Id()</c>/<c>Description()</c> chain (which targets the LAST OUTPUT) does not apply;
    /// the attributes land on the branch definition itself (Р12: common to all elements).
    /// </summary>
    public void ApplyBranchIdentity(XElement element, IRouteDefinition definition)
    {
        if (definition is not Definitions.ProcessorDefinition step)
            return;
        if (element.Attribute("id")?.Value is { Length: > 0 } id)
            step.StepId = id;
        if (element.Attribute("description")?.Value is { Length: > 0 } description)
            step.StepDescription = description;
    }

    // ── Endpoint URIs (the load-time scheme check, Ф0 §7.1) ──────────────────

    /// <summary>
    /// Notes an endpoint URI for the post-parse scheme check. URIs led by <c>{{…}}</c> or
    /// <c>${…}</c> are skipped — their scheme is known later.
    /// </summary>
    public void NoteEndpointUri(XElement element, string uri)
    {
        if (uri.StartsWith("{{", StringComparison.Ordinal) || uri.StartsWith("${", StringComparison.Ordinal))
            return;
        _endpointUris.Add((element, uri));
    }

    /// <summary>Checks every noted literal scheme against the context's registered components.</summary>
    /// <summary>
    /// One rule for the whole document: every element must live in the route namespace — an
    /// element from another (or the empty) namespace never passes as a core element on a
    /// local-name match. Package namespaces will widen the allowed set together with the Ф4
    /// catalog/XSD; until then the rule keeps every written document XSD-clean.
    /// </summary>
    internal void CheckElementNamespaces(XElement root)
    {
        foreach (var element in root.Descendants())
        {
            if (element.Name.NamespaceName != XmlRouteLoader.Namespace)
                AddError(element, $"element <{element.Name.LocalName}> is in namespace " +
                                  $"'{element.Name.NamespaceName}'; only the route namespace " +
                                  $"'{XmlRouteLoader.Namespace}' is readable.");
        }
    }

    internal void CheckSchemes()
    {
        var known = new HashSet<string>(RouteContext.GetComponentNames(), StringComparer.OrdinalIgnoreCase);
        foreach (var (element, uri) in _endpointUris)
        {
            var separator = uri.IndexOf(':');
            if (separator <= 0)
                continue; // not a scheme-shaped URI; the endpoint parser will speak to it
            var scheme = uri[..separator];
            if (scheme.Contains("{{") || scheme.Contains("${"))
                continue;
            if (!known.Contains(scheme))
                AddError(element,
                    $"scheme '{scheme}' is not registered in the context. Reference the connector " +
                    $"package and register its component (registered: {string.Join(", ", known.OrderBy(n => n))}).");
        }
    }
}

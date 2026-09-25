using System.Xml.Linq;
using redb.Route.Abstractions;

namespace redb.Route.Xml;

/// <summary>
/// What kind of element a contribution declares. The kind is metadata for the schema and the
/// catalog (Ф4) and for the editor; dispatch itself is uniform — every contribution gets full
/// control of its element through <see cref="IXmlElementContribution.Apply"/> and the traversal
/// helpers on <see cref="XmlParseContext"/>.
/// </summary>
public enum XmlElementKind
{
    /// <summary>A leaf step (<c>&lt;to&gt;</c>, <c>&lt;setHeader&gt;</c>).</summary>
    Step,

    /// <summary>A scope whose children are steps (<c>&lt;filter&gt;</c>, <c>&lt;split&gt;</c>).</summary>
    Scope,

    /// <summary>A node whose children are branches, not steps (<c>&lt;choice&gt;</c>).</summary>
    Branching,

    /// <summary>A node whose children configure it rather than being steps (rich <c>&lt;log&gt;</c>).</summary>
    ConfigChild,

    /// <summary>An element that lives beside <c>&lt;route&gt;</c> (<c>&lt;rest&gt;</c>, future).</summary>
    TopLevel,

    /// <summary>
    /// An element that lives inside <c>&lt;context&gt;</c> beside <c>&lt;components&gt;</c>/
    /// <c>&lt;bean&gt;</c>/<c>&lt;onInit&gt;</c> (the redb bridge above all). Never a step.
    /// </summary>
    ContextLevel,
}

/// <summary>The value kind of one XML attribute — what the schema and the editor say about it.</summary>
public enum AttributeType
{
    /// <summary>A literal string (a name, a key, a literal value).</summary>
    String,
    /// <summary>A route-language expression or <c>${…}</c> template — a string to the schema, annotated for the editor.</summary>
    Expression,
    /// <summary>An endpoint URI (may carry <c>{{…}}</c> placeholders).</summary>
    Uri,
    /// <summary>A <c>#name</c> reference into the context registry.</summary>
    Reference,
    /// <summary>A CLR type name (<c>Full.Name, Assembly</c>).</summary>
    TypeName,
    /// <summary><c>true</c>/<c>false</c>.</summary>
    Bool,
    /// <summary>A whole number.</summary>
    Int,
    /// <summary>A whole number, 64-bit.</summary>
    Long,
    /// <summary>A floating-point number, invariant culture.</summary>
    Double,
    /// <summary>A <see cref="TimeSpan"/> (<c>hh:mm:ss</c> / <c>hh:mm:ss.fff</c>).</summary>
    Duration,
    /// <summary>One of a fixed set of names — <see cref="AttributeSpec.EnumValues"/>.</summary>
    Enum,
    /// <summary>
    /// A comma-separated list of CLR type names (<c>exceptions=</c>): the comma SEPARATES entries,
    /// so an entry is a full name without an assembly suffix. Distinct from <see cref="TypeName"/>
    /// because the pack gate resolves each entry, and an assembly-qualified single name also has
    /// a comma. Appended LAST on purpose: package contributions compiled against an earlier
    /// version store the numeric values of the members above, so an insertion would shift them.
    /// </summary>
    TypeNameList,
}

/// <summary>One attribute of a format element — the schema/catalog/editor description (Ф4 §2).</summary>
public sealed record AttributeSpec(
    string Name,
    AttributeType Type,
    bool Required = false,
    IReadOnlyList<string>? EnumValues = null)
{
    /// <summary>
    /// The attribute is read as a CONDITION (a predicate), not as a value: <c>&lt;filter expr&gt;</c>,
    /// <c>&lt;when expr&gt;</c>, <c>&lt;loop while&gt;</c>. The pack gate reads this to warn about a
    /// condition written as a template, which is true whatever it renders. An init property, never a
    /// positional parameter: a released package contribution is compiled against this record.
    /// </summary>
    public bool Condition { get; init; }

    /// <summary>
    /// The attribute names a FILE the runtime reads from the package's <c>resources/</c> — a
    /// template, a transformation spec. The pack gate checks the file is there, exactly as it
    /// checks the format's own <c>file=</c>; a package element whose attribute is named
    /// differently (<c>&lt;payload template&gt;</c>, <c>&lt;transformJson spec&gt;</c>) says so
    /// here. An init property, never a positional parameter: a released contribution compiled
    /// against this record keeps working, it only goes unchecked until it opts in.
    /// </summary>
    public bool Resource { get; init; }
}

/// <summary>
/// The public description of one format element (Ф4 §2): the single source the parser hints,
/// the XSD generator and the catalog all read, so they cannot drift apart. Branch and
/// configuration children (<c>&lt;when&gt;</c>, <c>&lt;param&gt;</c>, …) are nested specs —
/// they are parsed inside their parent's contribution and have no registry entry of their own.
/// </summary>
public sealed record ElementSpec(
    string Name,
    XmlElementKind Kind,
    IReadOnlyList<AttributeSpec> Attributes,
    IReadOnlyList<ElementSpec> Children,
    bool AllowsSteps = false,
    bool AllowsTextContent = false,
    bool TakesEndpoint = false)
{
    /// <summary>
    /// The step ends the route for the exchange (<c>stop</c>, <c>rollbackAll</c>,
    /// <c>throwException</c>): a step written after it in the same list never runs, and the pack
    /// gate says so. An init-only property, not a constructor parameter, so a package
    /// contribution compiled against an earlier version keeps binding.
    /// </summary>
    public bool Terminal { get; init; }

    /// <summary>A leaf with attributes only.</summary>
    public static ElementSpec Leaf(string name, params AttributeSpec[] attributes)
        => new(name, XmlElementKind.Step, attributes, []);

    /// <summary>A scope: attributes plus the step group as content.</summary>
    public static ElementSpec Scope(string name, params AttributeSpec[] attributes)
        => new(name, XmlElementKind.Scope, attributes, [], AllowsSteps: true);

    /// <summary>A configuration/branch child, declared inline by its parent.</summary>
    public static ElementSpec Child(string name, bool allowsSteps, params AttributeSpec[] attributes)
        => new(name, XmlElementKind.ConfigChild, attributes, [], AllowsSteps: allowsSteps);
}

/// <summary>
/// One XML element of the route format (Р21): its name, kind, and the parse action that turns the
/// element into calls on the existing fluent DSL. The core registers its own elements through
/// this same contract; a package with DSL of its own (`Cache`, `Templates`, …) registers its
/// elements via <see cref="XmlRouteLoaderOptions.Extensions"/>. A contribution only calls DSL —
/// it never carries semantics of its own (Р1).
/// </summary>
public interface IXmlElementContribution
{
    /// <summary>The element's local name (<c>to</c>, <c>filter</c>). Case-sensitive, camelCase.</summary>
    string Name { get; }

    /// <summary>The element's kind — metadata for the schema, catalog and editor.</summary>
    XmlElementKind Kind { get; }

    /// <summary>
    /// The element's public description for the schema, the catalog and the editor (Ф4 §2). The
    /// default is an opaque spec (name and kind, nothing else) so existing contributions keep
    /// working; a contribution that wants autocompletion and validation declares the full shape.
    /// </summary>
    ElementSpec Spec => new(Name, Kind, [], [], AllowsSteps: Kind is XmlElementKind.Scope);

    /// <summary>
    /// Applies the element to the route under construction and returns the current position for
    /// the next sibling. A scope opens itself, parses its children through
    /// <see cref="XmlParseContext.ParseSteps"/>, and returns the closed scope's parent. On a
    /// recoverable problem the contribution records it via <see cref="XmlParseContext.AddError"/>
    /// and returns <paramref name="current"/> unchanged — the traversal collects every error of
    /// the document in one pass.
    /// </summary>
    IRouteDefinition Apply(XElement element, IRouteDefinition current, XmlParseContext context);

    /// <summary>
    /// Prints the element as the fluent C# it parses into (Ф6) — the third face of the
    /// contribution beside <see cref="Apply"/> and <see cref="Spec"/>, so the parser, the
    /// schema and the generator cannot drift apart. The default refuses LOUDLY: a package
    /// element without a printer is an explicit generator error, never a silent skip.
    /// </summary>
    void Print(XElement element, XmlCodeWriter code) => throw new NotSupportedException(
        $"element <{Name}> is not supported by the code generator " +
        $"(contribution {GetType().FullName} declares no printer).");

    /// <summary>
    /// The namespaces the printed C# needs (<c>using</c> lines the generator adds to the file
    /// shell when this contribution is registered). Empty by default — a printer whose verbs
    /// live outside the core namespaces declares its own.
    /// </summary>
    IReadOnlyList<string> GeneratedUsings => [];
}

/// <summary>
/// A contribution whose element lives at the container level, beside <c>&lt;route&gt;</c>
/// (<see cref="XmlElementKind.TopLevel"/> — the REST DSL above all): it applies to the route
/// BUILDER, not to a route under construction. Parsed with the container handlers, before the
/// route elements, inside the builder's Configure.
/// </summary>
public interface IXmlTopLevelContribution : IXmlElementContribution
{
    /// <summary>Applies the container-level element to the builder.</summary>
    void ApplyTopLevel(XElement element, redb.Route.Core.RouteBuilder builder, XmlParseContext context);
}

/// <summary>
/// A contribution whose element lives at the CONTEXT level, inside <c>&lt;context&gt;</c>
/// (<see cref="XmlElementKind.ContextLevel"/> — the redb bridge above all): it applies to the
/// route CONTEXT during <c>context.xml</c> loading, before any route document. Parsed by
/// <c>XmlContextLoader</c>; in route position the element is an error.
/// </summary>
public interface IXmlContextContribution : IXmlElementContribution
{
    /// <summary>Applies the context-level element to the context being configured.</summary>
    void ApplyContext(XElement element, redb.Route.Core.RouteContext context, XmlParseContext parseContext);
}

/// <summary>Options of the loader. Extensions are the Р21 registration channel for packages.</summary>
public sealed class XmlRouteLoaderOptions
{
    /// <summary>Element contributions supplied by packages, merged into the core registry.</summary>
    public IReadOnlyList<IXmlElementContribution> Extensions { get; init; } = [];

    /// <summary>
    /// Validates every document against the registry's generated XSD during load (Ф4 §3.4, on
    /// by default). The parser's own findings win on a shared line; the schema adds what the
    /// parser deliberately leaves to it — an unqualified attribute typo above all. Turn off for
    /// documents that carry a newer schema's <c>##other</c> extensions on purpose.
    /// </summary>
    public bool ValidateAgainstSchema { get; init; } = true;
}

/// <summary>
/// Every problem of one document, collected in one pass — file, line and column per entry, so
/// fixing ten typos does not take ten runs.
/// </summary>
public sealed class XmlRouteException : Exception
{
    /// <summary>The individual problems, each carrying its position.</summary>
    public IReadOnlyList<string> Errors { get; }

    internal XmlRouteException(string sourceName, IReadOnlyList<string> errors)
        : base($"{sourceName}: {errors.Count} error(s) in the route document:{Environment.NewLine}" +
               string.Join(Environment.NewLine, errors))
        => Errors = errors;
}

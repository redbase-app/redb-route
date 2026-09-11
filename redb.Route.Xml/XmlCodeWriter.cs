using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace redb.Route.Xml;

/// <summary>How the generated C# is shaped (Ф6 §2).</summary>
public enum CodeGenStyle
{
    /// <summary>For a human taking the code over: comments from descriptions, clean layout.</summary>
    Readable,
    /// <summary>For a build step (AOT): <c>#line</c> directives map diagnostics back to the XML.</summary>
    Machine,
}

/// <summary>
/// The emitting side of the Ф6 generator: indentation, fluent-verb lines, literal rules
/// (§3.2 — raw strings for anything with quotes or templates, never manual escaping) and typed
/// arguments (§3.3 — <see cref="TimeSpan"/>s, enums and types print as code, not strings).
/// Contributions print their element through this writer and recurse into children via
/// <see cref="PrintSteps"/>.
/// </summary>
public sealed class XmlCodeWriter
{
    private readonly StringBuilder _text = new();
    private readonly ElementRegistry _registry;
    private readonly string _sourceName;
    private readonly string? _resourceRoot;
    private int _indent;

    internal XmlCodeWriter(ElementRegistry registry, CodeGenStyle style, string sourceName, string? resourceRoot)
    {
        _registry = registry;
        Style = style;
        _sourceName = sourceName;
        _resourceRoot = resourceRoot;
    }

    /// <summary>
    /// Reads a file the way the loader would (schema bodies referenced by <c>file=</c>): the
    /// generator inlines the content into the generated code.
    /// </summary>
    public string ReadResource(string reference)
    {
        var path = Path.IsPathRooted(reference) || _resourceRoot is null
            ? reference
            : Path.Combine(_resourceRoot, reference);
        return File.ReadAllText(path);
    }

    /// <summary>The requested output style.</summary>
    public CodeGenStyle Style { get; }

    internal string Text => _text.ToString();

    // ── lines and indentation ────────────────────────────────────────────────

    /// <summary>Writes one line at the current indentation.</summary>
    public XmlCodeWriter Line(string text)
    {
        _text.Append(' ', _indent * 4).Append(text).Append('\n');
        return this;
    }

    /// <summary>Writes an empty line.</summary>
    public XmlCodeWriter BlankLine()
    {
        _text.Append('\n');
        return this;
    }

    /// <summary>Increases indentation for a nested block.</summary>
    public XmlCodeWriter Indent() { _indent++; return this; }

    /// <summary>Decreases indentation.</summary>
    public XmlCodeWriter Outdent() { _indent--; return this; }

    /// <summary>
    /// In machine style, a <c>#line</c> directive pointing diagnostics at the XML element
    /// (Razor's trick, §2); a no-op in readable style. Directives ignore indentation.
    /// </summary>
    public XmlCodeWriter MapTo(XElement element)
    {
        if (Style == CodeGenStyle.Machine && ((IXmlLineInfo)element).HasLineInfo())
            _text.Append("#line ").Append(((IXmlLineInfo)element).LineNumber)
                 .Append(" \"").Append(_sourceName.Replace("\\", "/", StringComparison.Ordinal)).Append("\"\n");
        return this;
    }

    // ── the statement model ──────────────────────────────────────────────────
    // Generated code is STATEMENTS against receiver variables, never one long chain: a chain
    // dies at the first scope closer (End* returns the IRouteDefinition facade and the concrete
    // members vanish), which is exactly why the Ф3 equivalence twins were written with
    // variables. A scope introduces a fresh variable; a typed section (ofType) does too and
    // simply never closes — the siblings keep using the parent receiver.

    private readonly Stack<string> _receivers = new();
    private int _nextVar;

    /// <summary>The current receiver variable the next statement targets.</summary>
    public string Receiver => _receivers.Peek();

    /// <summary>Pushes the root receiver (the route or builder-level definition variable).</summary>
    public string PushRoot(string declaration, XElement element, string prefix)
    {
        var name = Fresh(prefix);
        MapTo(element);
        Line($"var {name} = {declaration};");
        _receivers.Push(name);
        return name;
    }

    /// <summary>Pops the current receiver.</summary>
    public void PopReceiver() => _receivers.Pop();

    private string Fresh(string prefix) => $"{prefix}{_nextVar++}";

    /// <summary>One statement on the current receiver: <c>recv.Verb(args);</c>.</summary>
    public XmlCodeWriter Verb(XElement element, string verb, params string[] args)
    {
        MapTo(element);
        return Line($"{Receiver}.{verb}({string.Join(", ", args)});");
    }

    /// <summary>A configuration statement on the current receiver (no element mapping).</summary>
    public XmlCodeWriter Config(string call) => Line($"{Receiver}.{call};");

    /// <summary>
    /// Prints a scope: a fresh variable opened from the current receiver, the children as
    /// statements on it, and the closing call. <paramref name="closeVerb"/> null = a typed
    /// section that never closes (ofType).
    /// </summary>
    public XmlCodeWriter Scope(XElement element, string openVerb, string[] args, string? closeVerb,
        Action<XmlCodeWriter>? body = null)
    {
        var name = Fresh("s");
        MapTo(element);
        Line($"var {name} = {Receiver}.{openVerb}({string.Join(", ", args)});");
        _receivers.Push(name);
        if (body is not null)
            body(this);
        else
            PrintSteps(element);
        _receivers.Pop();
        if (closeVerb is not null)
            Line($"{name}.{closeVerb}();");
        return this;
    }

    /// <summary>Opens a branch child (<c>when</c>/<c>catch</c>) as a fresh variable on the current receiver.</summary>
    public string OpenBranch(XElement element, string verb, params string[] args)
    {
        var name = Fresh("b");
        MapTo(element);
        Line($"var {name} = {Receiver}.{verb}({string.Join(", ", args)});");
        if (element.Attribute("id")?.Value is { Length: > 0 })
            throw new NotSupportedException(
                $"id= on a branch child <{element.Name.LocalName}> is not supported by the code generator " +
                "(the fluent DSL has no public seam to set a branch StepId before its steps).");
        if (element.Attribute("description")?.Value is { Length: > 0 } description)
            Line($"{name}.Description({Str(description)});");
        _receivers.Push(name);
        return name;
    }

    /// <summary>Closes a branch opened by <see cref="OpenBranch"/>.</summary>
    public void CloseBranch(string name, string? closeVerb = null)
    {
        _receivers.Pop();
        if (closeVerb is not null)
            Line($"{name}.{closeVerb}();");
    }

    /// <summary>Prints every child element as a step through its registered contribution.</summary>
    public XmlCodeWriter PrintSteps(XElement parent)
    {
        foreach (var child in parent.Elements())
            PrintStep(child);
        return this;
    }

    /// <summary>Prints one element through its registered contribution.</summary>
    public XmlCodeWriter PrintStep(XElement element)
    {
        var contribution = _registry.Find(element.Name.LocalName)
            ?? throw new NotSupportedException(
                $"element <{element.Name.LocalName}> is not in the registry — the parser would refuse it too.");
        if (Style == CodeGenStyle.Readable && element.Attribute("description")?.Value is { Length: > 0 } description)
            Line($"// {description}");
        contribution.Print(element, this);
        PrintIdentity(element);
        return this;
    }

    /// <summary>
    /// The trailing identity the parser applies (Р12): <c>Id()</c>/<c>Description()</c> on the
    /// receiver target its LAST output — the step just printed — exactly like the loader.
    /// </summary>
    public XmlCodeWriter PrintIdentity(XElement element)
    {
        if (element.Attribute("id")?.Value is { Length: > 0 } id)
            Config($"Id({Str(id)})");
        if (element.Attribute("description")?.Value is { Length: > 0 } description)
            Config($"Description({Str(description)})");
        return this;
    }

    // ── literals (§3.2–3.3) ──────────────────────────────────────────────────

    /// <summary>A C# string literal: plain when harmless, raw <c>"""…"""</c> otherwise.</summary>
    public static string Str(string value)
    {
        if (!value.Contains('"') && !value.Contains('\\') && !value.Contains('\n') && !value.Contains('\r'))
            return $"\"{value}\"";
        if (!value.Contains('\n') && !value.Contains('\r') && !value.Contains("\"\"\""))
            return $"\"\"\"{value}\"\"\"";
        var cleaned = value.Replace("\r", "", StringComparison.Ordinal);
        return "\"\"\"\n" + cleaned + "\n\"\"\"";
    }

    /// <summary>An expression argument: the route-language string wrapped in <c>Expr(...)</c>.</summary>
    public static string Expr(string template) => $"Expr({Str(template)})";

    /// <summary>A <see cref="TimeSpan"/> as code (§3.3): round values by unit, otherwise Parse.</summary>
    public static string Ts(TimeSpan value)
    {
        if (value.Ticks % TimeSpan.TicksPerSecond == 0 && value.TotalSeconds is >= 1 and < 1000000)
            return $"TimeSpan.FromSeconds({value.TotalSeconds.ToString(CultureInfo.InvariantCulture)})";
        if (value.Ticks % TimeSpan.TicksPerMillisecond == 0 && value.TotalMilliseconds is >= 1 and < 1000000)
            return $"TimeSpan.FromMilliseconds({value.TotalMilliseconds.ToString(CultureInfo.InvariantCulture)})";
        return $"TimeSpan.Parse({Str(value.ToString("c", CultureInfo.InvariantCulture))}, System.Globalization.CultureInfo.InvariantCulture)";
    }

    /// <summary>A <c>typeof(...)</c> when the type resolves now; the runtime lookup otherwise.</summary>
    public static string TypeRef(string typeName)
    {
        var resolved = Components.Bean.DefaultBeanTypeResolver.Instance.Resolve(typeName);
        return resolved?.FullName is { } fullName && !fullName.Contains('`')
            ? $"typeof(global::{fullName})"
            : $"Type.GetType({Str(typeName)}, throwOnError: true)!";
    }

    /// <summary>A bool literal.</summary>
    public static string Bool(bool value) => value ? "true" : "false";

    /// <summary>An enum member reference.</summary>
    public static string EnumRef<T>(T value) where T : struct, Enum => $"{typeof(T).Name}.{value}";
}

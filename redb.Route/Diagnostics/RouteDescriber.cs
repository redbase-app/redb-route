using System.Globalization;
using System.Reflection;
using System.Text;
using redb.Route.Abstractions;

namespace redb.Route.Diagnostics;

/// <summary>
/// Renders a definition tree as stable, diffable, human-readable text (Route-XML Ф3 §3): one
/// line per node — the node kind and its significant parameters — children indented beneath.
/// Three consumers share it: XML-vs-C# equivalence tests, the Ф6 code generator's round-trip
/// check, and the Mermaid renderer, so it is deliberately generic: parameters come from the
/// definition's own fields by reflection (cold path), never from a hand-kept switch that would
/// drift from the definition set.
/// </summary>
/// <remarks>
/// What a line shows: strings, numbers, booleans, enums, <see cref="TimeSpan"/>s, types,
/// expressions (their template text) and string collections — sorted by name for determinism.
/// What it never shows: delegates, live objects, hash codes — things that legitimately differ
/// between two constructions of the same route. A condition is rendered through
/// <see cref="IConditionSource"/>, the text the author wrote.
/// </remarks>
public static class RouteDescriber
{
    /// <summary>Describes one definition tree. Two equal trees produce byte-equal text.</summary>
    public static string Describe(IProcessorDefinition root)
    {
        ArgumentNullException.ThrowIfNull(root);
        var builder = new StringBuilder();
        Append(root, builder, 0);
        return builder.ToString();
    }

    /// <summary>Describes every route of a context, in registration order.</summary>
    public static string Describe(IEnumerable<IProcessorDefinition> roots)
    {
        ArgumentNullException.ThrowIfNull(roots);
        var builder = new StringBuilder();
        foreach (var root in roots)
            Append(root, builder, 0);
        return builder.ToString();
    }

    private static void Append(IProcessorDefinition node, StringBuilder builder, int depth)
    {
        builder.Append(' ', depth * 2).Append(Line(node)).Append('\n');
        foreach (var child in node.Outputs)
            Append(child, builder, depth + 1);
        if (node is IBranchingDefinition branching)
            foreach (var branch in branching.Branches)
                Append(branch, builder, depth + 1);
    }

    private static string Line(IProcessorDefinition node)
    {
        var type = node.GetType();
        var line = new StringBuilder(Label(type));

        if (node is IConditionSource source)
        {
            var condition = source.SourceTemplate ?? source.SourceExpression?.ToTemplateString();
            if (!string.IsNullOrEmpty(condition))
                line.Append(" condition=").Append(Escape(condition));
        }

        foreach (var (name, value) in Parameters(node, type))
            line.Append(' ').Append(name).Append('=').Append(Escape(value));

        return line.ToString();
    }

    /// <summary>The node kind: the type name without the Definition suffix, camelCased; generic arguments by name.</summary>
    private static string Label(Type type)
    {
        var name = type.Name;
        var tick = name.IndexOf('`');
        if (tick >= 0)
            name = name[..tick];
        if (name.EndsWith("Definition", StringComparison.Ordinal))
            name = name[..^"Definition".Length];
        if (name.Length > 0 && char.IsUpper(name[0]))
            name = char.ToLowerInvariant(name[0]) + name[1..];
        if (type.IsGenericType)
            name += "<" + string.Join(",", type.GetGenericArguments().Select(a => a.Name)) + ">";
        return name;
    }

    private static IEnumerable<(string Name, string Value)> Parameters(object node, Type type)
    {
        var seen = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var isRoute = node is Definitions.RouteDefinition;
        for (var current = type; current is not null && current != typeof(object); current = current.BaseType)
        {
            // Route-level knobs (autoStart, cluster, …) are declared on the shared base every
            // scope inherits; on a scope node they are constant noise — only the route line
            // carries them.
            var isSharedBase = current.IsGenericType
                && current.GetGenericTypeDefinition().Name == "RouteDefinitionBase`1";
            if (isSharedBase && !isRoute)
                continue;
            foreach (var field in current.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                var name = NormalizeName(field.Name);
                if (name is null || seen.ContainsKey(name))
                    continue;
                var rendered = Render(field.GetValue(node));
                if (rendered is not null)
                    seen[name] = rendered;
            }
        }
        return seen.Select(p => (p.Key, p.Value));
    }

    /// <summary>
    /// <c>_uri</c> → <c>uri</c>, <c>&lt;StepId&gt;k__BackingField</c> → <c>stepId</c>;
    /// null for names that carry no configuration.
    /// </summary>
    private static string? NormalizeName(string fieldName)
    {
        var name = fieldName;
        if (name.StartsWith('<'))
        {
            var close = name.IndexOf('>');
            if (close <= 1)
                return null;
            name = name[1..close];
        }
        name = name.TrimStart('_');
        if (name.Length == 0)
            return null;
        name = char.ToLowerInvariant(name[0]) + name[1..];
        // Structure, not configuration: children and back-links are the tree itself; the
        // source* trio is already rendered once as condition= through IConditionSource.
        return name is "outputs" or "parent" or "context"
            or "sourceTemplate" or "sourceExpression" or "sourcePredicate" ? null : name;
    }

    /// <summary>A stable rendering of one value, or null for what must not be compared.</summary>
    private static string? Render(object? value) => value switch
    {
        null => null,
        string text => text,
        bool flag => flag ? "true" : "false",
        Enum member => member.ToString(),
        TimeSpan span => span.ToString("c", CultureInfo.InvariantCulture),
        Type type => type.FullName ?? type.Name,
        IExpression expression => expression.ToTemplateString(),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        IReadOnlyDictionary<string, int> weights => string.Join(",",
            weights.OrderBy(w => w.Key, StringComparer.Ordinal).Select(w => $"{w.Key}={w.Value.ToString(CultureInfo.InvariantCulture)}")),
        IEnumerable<string> items => string.Join(",", items),
        Delegate => null,
        _ => null, // live instances (predicates, repositories, processors) legitimately differ
    };

    private static string Escape(string value)
        => value.Replace("\r", "", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);
}

using System.Text;
using redb.Route.Abstractions;

namespace redb.Route.Diagnostics;

/// <summary>
/// Renders definition trees as a Mermaid flowchart (Route-XML Ф6 §6) — documentation diagrams
/// generated from the same source as the running route, so they cannot go stale. Works over the
/// definition tree, which means both spellings of a route — XML and fluent C# — draw
/// identically. Deliberately simple: horizontal flow, node categories differ by shape,
/// conditions carry their authored text; it is not a visual editor and not its prototype.
/// </summary>
public static class MermaidRenderer
{
    /// <summary>
    /// Renders the routes as one flowchart. Top-down by default: Mermaid cannot wrap a long
    /// chain (its layout owns the ranks), so a vertical flow is the honest way to keep a big
    /// route on screen — it scrolls like text (live finding on mermaid.live, 2026-09-09);
    /// pass <paramref name="direction"/> "LR" for the horizontal spelling.
    /// </summary>
    public static string Render(IEnumerable<IProcessorDefinition> roots, string direction = "TD")
    {
        ArgumentNullException.ThrowIfNull(roots);
        var builder = new StringBuilder("flowchart ").Append(direction).Append('\n');
        var counter = 0;
        foreach (var root in roots)
        {
            var entry = NextId(ref counter);
            builder.Append("  ").Append(entry).Append("[\"").Append(Escape(RouteLabel(root))).Append("\"]\n");
            RenderChildren(root, entry, builder, ref counter);
        }
        return builder.ToString();
    }

    private static void RenderChildren(IProcessorDefinition node, string previous, StringBuilder builder, ref int counter)
    {
        IReadOnlyList<string> exits = [previous];
        foreach (var child in node.Outputs)
            exits = RenderNode(child, exits, builder, ref counter, dotted: false);
        if (node is IBranchingDefinition branching)
            foreach (var branch in branching.Branches)
                RenderNode(branch, exits, builder, ref counter, dotted: true);
    }

    private static IReadOnlyList<string> RenderNode(IProcessorDefinition node, IReadOnlyList<string> previous, StringBuilder builder, ref int counter, bool dotted)
    {
        var id = NextId(ref counter);
        var (open, close) = Shape(node);
        builder.Append("  ").Append(id).Append(open).Append(Escape(Label(node))).Append(close).Append('\n');
        foreach (var from in previous)
            builder.Append("  ").Append(from).Append(dotted ? " -.-> " : " --> ").Append(id).Append('\n');

        // A branching node (choice) draws its branches as dotted fan-out from itself; the
        // continuation then merges from every branch TAIL — a solid edge out of the diamond
        // itself would read as one more branch (owner finding on mermaid.live, 2026-09-08).
        if (node is IBranchingDefinition branching)
        {
            var tails = new List<string>();
            foreach (var branch in branching.Branches)
                tails.AddRange(RenderNode(branch, [id], builder, ref counter, dotted: true));
            IReadOnlyList<string> exits = tails.Count > 0 ? tails : [id];
            foreach (var child in node.Outputs)
                exits = RenderNode(child, exits, builder, ref counter, dotted: false);
            return exits;
        }
        IReadOnlyList<string> inner = [id];
        foreach (var child in node.Outputs)
            inner = RenderNode(child, inner, builder, ref counter, dotted: false);
        return inner;
    }

    private static string NextId(ref int counter) => $"n{counter++}";

    private static string RouteLabel(IProcessorDefinition root)
    {
        var kind = Kind(root);
        if (root is IRouteDefinition route)
        {
            var id = route.GetRouteId();
            var from = route.GetFromUri();
            var label = id ?? kind;
            return from is { Length: > 0 } ? $"{label}: {from}" : label;
        }
        return kind;
    }

    private static string Label(IProcessorDefinition node)
    {
        var kind = Kind(node);
        if (node is IConditionSource { } source)
        {
            var condition = source.SourceTemplate ?? source.SourceExpression?.ToTemplateString();
            if (!string.IsNullOrEmpty(condition))
                return $"{kind}: {condition}";
        }
        if (node is Definitions.ToDefinition to)
            return to.Uri;
        return kind;
    }

    private static string Kind(IProcessorDefinition node)
    {
        var name = node.GetType().Name;
        var tick = name.IndexOf('`');
        if (tick >= 0)
            name = name[..tick];
        if (name.EndsWith("Definition", StringComparison.Ordinal))
            name = name[..^"Definition".Length];
        return name.Length > 0 ? char.ToLowerInvariant(name[0]) + name[1..] : name;
    }

    // Every label is QUOTED: inside "…" Mermaid reads the text literally, so URIs with
    // {{placeholders}}, colons and parentheses survive (an unquoted ((…)) parses as a circle
    // shape — the live finding on mermaid.live, 2026-09-09).
    private static (string Open, string Close) Shape(IProcessorDefinition node) => node switch
    {
        Definitions.ToDefinition => ("[[\"", "\"]]"),                   // endpoint
        IConditionSource => ("{{\"", "\"}}"),                           // condition
        IBranchingDefinition => ("{\"", "\"}"),                         // branching
        _ when node.Outputs.Count > 0 => ("([\"", "\"])"),              // scope
        _ => ("[\"", "\"]"),                                            // leaf step
    };

    private static string Escape(string label)
    {
        var cleaned = label
            .Replace("\r", "", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            // Mermaid entities: a literal # starts one, a quote would end the label.
            .Replace("#", "#35;", StringComparison.Ordinal)
            .Replace("\"", "#quot;", StringComparison.Ordinal);
        return cleaned.Length > 60 ? cleaned[..57] + "..." : cleaned;
    }
}

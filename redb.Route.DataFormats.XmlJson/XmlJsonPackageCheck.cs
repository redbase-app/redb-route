using redb.Route.Validation;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;
using redb.Route.DataFormats.XmlJson.Internal;
using redb.Route.Xml.Packaging;

namespace redb.Route.DataFormats.XmlJson;

/// <summary>
/// The xmljson checks of <c>check</c> and <c>pack</c> that need the format's own code, run with <c>--bin</c>: the schema
/// compiles with the reader the format uses at run time; each <c>arrays=</c> path leads through the schema's structure,
/// from a global element for <c>/a/b</c> and from any element for <c>//a/b</c>; <c>xs:any</c> and mixed content are
/// flagged; with <c>plural=true</c> the parent/child pairs the rule reads as lists are named. The checks that need no
/// code of the format (the schema is in the package, the paths are well formed) are the gate's own.
/// </summary>
public sealed class XmlJsonPackageCheck : IPackageCheck
{
    private static readonly string FormatType = typeof(XmlJsonDataFormat).FullName!;

    /// <inheritdoc />
    public void Check(PackageCheckContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        foreach (var document in context.Documents)
            foreach (var element in document.Document.Descendants())
                if (element.Name.LocalName is "dataFormat" or "marshal" or "unmarshal"
                    && element.Attribute("type")?.Value is { } type
                    && string.Equals(type.Split(',')[0].Trim(), FormatType, StringComparison.Ordinal))
                    CheckFormat(context, document, element);
    }

    private static void CheckFormat(PackageCheckContext context, PackageDocument document, XElement element)
    {
        if (element.Attribute("schema") is not { } schema
            || schema.Value.Contains("{{", StringComparison.Ordinal) || schema.Value.Contains("${", StringComparison.Ordinal)
            || context.Resource(schema.Value, out _) is not { } path)
            return; // no schema, one configuration resolves later, or one the gate already reports

        var name = schema.Value;
        XmlSchemaSet set;
        try
        {
            set = SchemaResolver.Load("xmljson", path, element.Attribute("schemaRoot")?.Value);
        }
        catch (Exception ex) when (ex is XmlSchemaException or XmlException or IOException or InvalidOperationException)
        {
            context.Error(document, schema, $"xmljson schema '{name}' does not compile: {ex.Message}");
            return;
        }

        var facts = SchemaFacts.Of(new SchemaModel(set), set);
        if (element.Attribute("arrays") is { } arrays)
            foreach (var arraysPath in arrays.Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                if (facts.PathProblem(arraysPath) is { } problem)
                    context.Warning(document, arrays, $"xmljson arrays path '{arraysPath}' does not lead through schema '{name}': {problem}.");

        if (facts.Wildcards.Count > 0)
            context.Warning(document, schema,
                $"xmljson schema '{name}' has xs:any in {Listed(facts.Wildcards)}: elements there are not described, so their arrays come from arrays= or the document.");
        if (facts.Mixed.Count > 0)
            context.Warning(document, schema,
                $"xmljson schema '{name}' has mixed content in {Listed(facts.Mixed)}: text between child elements is joined into one text value.");

        if (element.Attribute("plural") is { } plural && string.Equals(plural.Value.Trim(), "true", StringComparison.OrdinalIgnoreCase))
            context.Warning(document, plural, facts.PluralPairs.Count == 0
                ? $"xmljson plural=true finds no parent named as the plural of its only child in schema '{name}'; the rule changes nothing here."
                : $"xmljson plural=true reads {facts.PluralPairs.Count} parent/child pair(s) of schema '{name}' as lists: " +
                  $"{Listed(facts.PluralPairs)}{(facts.PluralOnce.Count > 0 ? $"; the schema allows one child only in {Listed(facts.PluralOnce)}" : "")}.");
    }

    private static string Listed(IReadOnlyCollection<string> names)
        => string.Join(", ", names.Take(5).Select(n => $"'{n}'")) + (names.Count > 5 ? $" and {names.Count - 5} more" : "");

    /// <summary>What the checks read from a compiled schema, gathered in one walk over its types.</summary>
    private sealed class SchemaFacts
    {
        private readonly SchemaModel _model;
        private readonly XmlSchemaSet _set;
        private readonly Dictionary<string, List<XmlSchemaType?>> _byName = new(StringComparer.Ordinal);

        private SchemaFacts(SchemaModel model, XmlSchemaSet set) => (_model, _set) = (model, set);

        public SortedSet<string> Wildcards { get; } = new(StringComparer.Ordinal);
        public SortedSet<string> Mixed { get; } = new(StringComparer.Ordinal);
        public SortedSet<string> PluralPairs { get; } = new(StringComparer.Ordinal);
        public SortedSet<string> PluralOnce { get; } = new(StringComparer.Ordinal);

        public static SchemaFacts Of(SchemaModel model, XmlSchemaSet set)
        {
            var facts = new SchemaFacts(model, set);
            var seen = new HashSet<(string, XmlSchemaType?)>();
            var queue = new Queue<(string Name, XmlSchemaType? Type)>();
            foreach (XmlSchemaElement global in set.GlobalElements.Values)
                queue.Enqueue((global.QualifiedName.Name, global.ElementSchemaType));
            while (queue.TryDequeue(out var item))
            {
                if (!seen.Add(item))
                    continue;
                facts.Add(item.Name, item.Type);
                foreach (var slot in Slots(model, item.Type))
                    if (slot.Declaration is { } declaration)
                        queue.Enqueue((declaration.QualifiedName.Name, declaration.ElementSchemaType));
            }
            return facts;
        }

        // The built-in types (xs:anyType holds a wildcard and is mixed) are the schema language's, not the author's.
        private static IReadOnlyList<ContentSlot> Slots(SchemaModel model, XmlSchemaType? type)
            => type is XmlSchemaComplexType complex && complex.QualifiedName.Namespace != XmlSchema.Namespace
                ? model.Slots(type)
                : [];

        private void Add(string name, XmlSchemaType? type)
        {
            if (!_byName.TryGetValue(name, out var types))
                _byName[name] = types = [];
            types.Add(type);

            var slots = Slots(_model, type);
            var owner = type is { QualifiedName.IsEmpty: false } ? type.QualifiedName.Name : name;
            if (type is XmlSchemaComplexType { IsMixed: true } complex && complex.QualifiedName.Namespace != XmlSchema.Namespace)
                Mixed.Add(owner);
            if (slots.Any(s => s.Wildcard))
                Wildcards.Add(owner);
            var children = slots.Where(s => !s.Wildcard).Select(s => s.Name.Name).Distinct(StringComparer.Ordinal).ToList();
            if (children is [var child] && !slots.Any(s => s.Wildcard) && Plural.IsPair(name, child))
            {
                PluralPairs.Add($"{name}/{child}");
                if (!slots.Any(s => s.Repeats))
                    PluralOnce.Add($"{name}/{child}");
            }
        }

        /// <summary>Why an arrays path leads nowhere in the schema; <c>null</c> when it leads to an element or into xs:any.</summary>
        public string? PathProblem(string path)
        {
            var anywhere = path.StartsWith("//", StringComparison.Ordinal);
            if (!anywhere && !path.StartsWith('/'))
                return null; // malformed: the gate's own error
            var written = path[(anywhere ? 2 : 1)..].Split('/');
            var steps = written.Select(Local).ToArray();
            if (steps.Any(s => s.Length == 0))
                return null;

            List<XmlSchemaType?> current = anywhere
                ? _byName.GetValueOrDefault(steps[0]) ?? []
                : [.. _set.GlobalElements.Values.Cast<XmlSchemaElement>()
                    .Where(e => e.QualifiedName.Name == steps[0]).Select(e => e.ElementSchemaType)];
            if (current.Count == 0)
                return anywhere ? $"'{written[0]}' names no element of it" : $"'{written[0]}' is not a global element of it";

            for (var i = 1; i < steps.Length; i++)
            {
                var next = new List<XmlSchemaType?>();
                var open = false;
                foreach (var type in current)
                {
                    if (type is null)
                    {
                        open = true; // content nothing describes
                        continue;
                    }
                    foreach (var slot in Slots(_model, type))
                        if (slot.Wildcard)
                            open = true;
                        else if (slot.Name.Name == steps[i])
                            next.Add(slot.Declaration?.ElementSchemaType);
                }
                if (next.Count == 0)
                    return open ? null : $"'{written[i]}' is not a child of '{written[i - 1]}'";
                current = next;
            }
            return null;
        }

        private static string Local(string step) => step.Contains(':') ? step[(step.IndexOf(':') + 1)..] : step;
    }
}

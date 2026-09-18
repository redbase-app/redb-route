using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using redb.Route.Sql.Mapping;

namespace redb.Route.Sql.Batch;

/// <summary>Result of looking a name up in a batch item.</summary>
internal enum ItemLookup
{
    /// <summary>The item has a value for the name, possibly null.</summary>
    Found,

    /// <summary>The item has named values, but none for this name.</summary>
    Missing,

    /// <summary>Several keys equal the name ignoring case and none equals it exactly.</summary>
    Ambiguous,

    /// <summary>The item is null, a scalar, a collection, an XML node or another .NET type: it has no named values at all.</summary>
    NoNamedValues,
}

/// <summary>
/// Reads a named value out of a batch item in whatever shape the route produced it: a dictionary (generic, read-only or
/// non-generic — CSV rows are <c>Dictionary&lt;string, string&gt;</c>), a JSON object (<see cref="JsonElement"/>,
/// <see cref="JsonObject"/>, or a <see cref="JsonDocument"/> whose root is one), or a POCO. A key equal to the name wins;
/// failing that, the single key equal to it ignoring case — two such keys name nothing. POCO properties match by the rule that
/// maps result columns to properties (<see cref="ColumnNameMatcher"/>).
/// <para>
/// A POCO is a type of the application: the properties of a .NET type (<see cref="XElement.Value"/>, <see cref="Uri.Host"/>,
/// <see cref="Stream.Length"/>) are not the fields of a record. An XML node carries no named values, as in Apache Camel, where
/// a DOM node is iterated but only a <c>Map</c> is looked up by key; its values are bound with <c>xpath</c> expressions.
/// </para>
/// </summary>
internal static class BatchItemAccessor
{
    private static readonly ConcurrentDictionary<Type, PropertyInfo[]?> PocoProperties = new();
    private static readonly ConcurrentDictionary<(Type Type, string Name), PropertyInfo?> PocoMatches = new();

    /// <summary>Looks <paramref name="name"/> up in a batch item: by key in a map or JSON object, by property in a POCO.</summary>
    internal static ItemLookup Find(object? item, string name, out object? value) => Find(item, name, readProperties: true, out value);

    /// <summary>
    /// Looks <paramref name="name"/> up by key only — in a map or a JSON object. This is how the body of a single statement is
    /// read, as in Apache Camel, where a body that converts to a <c>Map</c> (Jackson's <c>JsonNode</c> included) is looked up
    /// by key and a POJO is not.
    /// </summary>
    internal static ItemLookup FindByKey(object? item, string name, out object? value) => Find(item, name, readProperties: false, out value);

    private static ItemLookup Find(object? item, string name, bool readProperties, out object? value)
    {
        value = null;
        switch (item)
        {
            case IDictionary<string, object?> dictionary:
                if (dictionary.TryGetValue(name, out value))
                    return ItemLookup.Found;
                return FindIgnoringCase(dictionary.Keys, name, key => dictionary[key], out value);

            case IReadOnlyDictionary<string, object?> readOnly:
                if (readOnly.TryGetValue(name, out value))
                    return ItemLookup.Found;
                return FindIgnoringCase(readOnly.Keys, name, key => readOnly[key], out value);

            case JsonElement { ValueKind: JsonValueKind.Object } json:
                if (json.TryGetProperty(name, out var property))
                {
                    value = property;
                    return ItemLookup.Found;
                }
                return FindIgnoringCase(json.EnumerateObject().Select(p => p.Name), name, key => json.GetProperty(key), out value);

            case JsonDocument document:
                return Find(document.RootElement, name, out value);

            case JsonObject jsonObject:
                if (jsonObject.TryGetPropertyValue(name, out var node))
                {
                    value = node;
                    return ItemLookup.Found;
                }
                return FindIgnoringCase(jsonObject.Select(p => p.Key), name, key => jsonObject[key], out value);

            case IDictionary nonGeneric:
                if (nonGeneric.Contains(name))
                {
                    value = nonGeneric[name];
                    return ItemLookup.Found;
                }
                return FindIgnoringCase(nonGeneric.Keys.OfType<string>(), name, key => nonGeneric[key], out value);

            case null:
            case JsonElement:
            case JsonNode:
            case XObject:
            case IEnumerable:
                return ItemLookup.NoNamedValues;

            default:
                return readProperties ? FindProperty(item, name, out value) : ItemLookup.NoNamedValues;
        }
    }

    /// <summary>The item is XML (a LINQ to XML object or a DOM node), whose values are reached with <c>xpath</c> expressions.</summary>
    internal static bool IsXml(object? item) => item is XObject or System.Xml.XmlNode;

    /// <summary>The entries of a dictionary item, which an item's <c>${header.name}</c> expressions see; nothing for other shapes.</summary>
    internal static IEnumerable<KeyValuePair<string, object?>> Entries(object? item) => item switch
    {
        IDictionary<string, object?> dictionary => dictionary,
        IReadOnlyDictionary<string, object?> readOnly => readOnly,
        IDictionary nonGeneric => nonGeneric.Keys.OfType<string>().Select(key => new KeyValuePair<string, object?>(key, nonGeneric[key])),
        _ => [],
    };

    private static ItemLookup FindIgnoringCase(IEnumerable<string> keys, string name, Func<string, object?> read, out object? value)
    {
        value = null;
        string? match = null;
        foreach (var key in keys)
        {
            if (!string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
                continue;
            if (match is not null)
                return ItemLookup.Ambiguous;
            match = key;
        }

        if (match is null)
            return ItemLookup.Missing;

        value = read(match);
        return ItemLookup.Found;
    }

    private static ItemLookup FindProperty(object item, string name, out object? value)
    {
        value = null;
        var type = item.GetType();
        var properties = PocoProperties.GetOrAdd(type, ReadableProperties);
        if (properties is null)
            return ItemLookup.NoNamedValues;

        var property = PocoMatches.GetOrAdd((type, name), static (key, all) => ColumnNameMatcher.Find(all, key.Name), properties);
        if (property is null)
            return ItemLookup.Missing;

        value = property.GetValue(item);
        return ItemLookup.Found;
    }

    /// <summary>
    /// Public readable instance properties of a POCO; null for a type that carries no named values: a scalar, or any type of
    /// .NET itself (<c>System.*</c>, <c>Microsoft.*</c>). Anonymous types and records of the application are POCOs.
    /// </summary>
    private static PropertyInfo[]? ReadableProperties(Type type)
    {
        if (type.IsPrimitive || type.IsEnum || IsFrameworkType(type))
            return null;

        var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetMethod is { IsPublic: true } && p.GetIndexParameters().Length == 0)
            .ToArray();
        return properties.Length == 0 ? null : properties;
    }

    private static bool IsFrameworkType(Type type) =>
        type.Namespace is { } ns
        && (ns == "System" || ns.StartsWith("System.", StringComparison.Ordinal)
            || ns == "Microsoft" || ns.StartsWith("Microsoft.", StringComparison.Ordinal));
}

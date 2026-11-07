using System.Globalization;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using redb.Route.Abstractions;

namespace redb.Route.Xml.CodeGen;

/// <summary>One data format of the editor catalog (<c>redb-route-dataformats.json</c>).</summary>
internal sealed record DataFormatEntry(
    string Type,
    string? Name,
    string ContentType,
    IReadOnlyList<string> MediaTypes,
    IReadOnlyList<DataFormatAttribute> Attributes,
    IReadOnlyList<string>? UnmarshalTargets);

/// <summary>One markup attribute of a data format: what the editor completes on <c>&lt;dataFormat&gt;</c> and the per-step <c>type=</c> form.</summary>
internal sealed record DataFormatAttribute(string Name, string Kind, IReadOnlyList<string>? EnumValues, string? Default, string Description);

/// <summary>
/// The data formats the editor offers, read from the assemblies a worker carries. The attributes follow the rule
/// <c>&lt;marshal type&gt;</c> and <c>&lt;dataFormat&gt;</c> bind by (Route-XML <c>CreateSerializer</c>): a format with a
/// parameterless constructor takes its own writable properties, one without takes those of the options object of its
/// single options constructor. Only the value types an attribute can carry are listed (string, bool, whole and decimal
/// numbers, time spans, enums).
/// <para>
/// Conventions, as nothing in the types says them: the name a format answers to in <c>format=</c> is its
/// <c>public const string Name</c>; a string property named <c>SchemaRoot</c>, ending in
/// <c>File</c> or <c>Path</c>, or listed in the data below, is of kind <c>Path</c>; the unmarshal targets are data too; a description is the
/// first sentence of the property's XML doc summary from the <c>.xml</c> file beside the assembly, else its name.
/// </para>
/// </summary>
internal static class DataFormatCatalog
{
    /// <summary>The unmarshal targets of the formats that accept a fixed set; the others take any type.</summary>
    private static readonly Dictionary<string, string[]> UnmarshalTargetsByType = new(StringComparer.Ordinal)
    {
        ["redb.Route.DataFormats.XmlJson.XmlJsonDataFormat"] =
        [
            "System.String", "System.Byte[]", "System.Xml.Linq.XDocument", "System.Xml.Linq.XElement",
            "System.Xml.XmlDocument", "System.Text.Json.Nodes.JsonNode, System.Text.Json",
        ],
    };

    /// <summary>
    /// Every data format in the <c>redb.Route*.dll</c> assemblies of <paramref name="binDirs"/> and of the tool itself
    /// that markup can name: one with a <c>public const string Name</c>, or one the engine registers by default (JSON,
    /// XML and the byte wrappers). Other serializers a connector keeps for itself (Telegram's update reader) are not data
    /// formats of the catalog. <paramref name="docDirs"/> are searched for an assembly's XML doc file when none lies
    /// beside it (a worker's shared layer ships no doc files).
    /// </summary>
    public static List<DataFormatEntry> Load(IEnumerable<string> binDirs, Func<string?, Assembly?> carried, IEnumerable<string>? docDirs = null)
    {
        _docDirs = docDirs?.ToList() ?? [];
        DocsCache.Clear();
        var assemblies = new List<Assembly> { typeof(IMessageSerializer).Assembly };
        foreach (var binDir in binDirs)
            assemblies.AddRange(LoadAssemblies(binDir, carried));
        return assemblies
            .Distinct()
            .SelectMany(Types)
            .Where(t => t is { IsPublic: true, IsAbstract: false, IsInterface: false } && typeof(IMessageSerializer).IsAssignableFrom(t))
            .Where(t => HasName(t) || DefaultTypes.Contains(t.FullName!))
            .DistinctBy(t => t.FullName)
            .Select(Describe)
            .OfType<DataFormatEntry>()
            .OrderBy(e => e.Name ?? "~" + e.Type, StringComparer.Ordinal)
            .ToList();
    }

    private static IEnumerable<Assembly> LoadAssemblies(string binDir, Func<string?, Assembly?> carried)
    {
        var context = new AssemblyLoadContext($"dataformats:{binDir}", isCollectible: false);
        context.Resolving += (loadContext, name) =>
        {
            if (carried(name.Name) is not null) return null;
            var candidate = Path.Combine(binDir, name.Name + ".dll");
            return File.Exists(candidate) ? loadContext.LoadFromAssemblyPath(candidate) : null;
        };
        foreach (var dll in Directory.GetFiles(binDir, "redb.Route*.dll"))
        {
            Assembly? assembly;
            try
            {
                var simpleName = AssemblyName.GetAssemblyName(dll).Name;
                assembly = carried(simpleName)
                           ?? context.Assemblies.FirstOrDefault(a => a.GetName().Name == simpleName)
                           ?? context.LoadFromAssemblyPath(dll);
            }
            catch (BadImageFormatException)
            {
                continue;
            }
            yield return assembly;
        }
    }

    private static IEnumerable<Type> Types(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(t => t is not null)!;
        }
    }

    /// <summary>The entry of <paramref name="type"/>, or <c>null</c> for a format markup cannot build.</summary>
    private static DataFormatEntry? Describe(Type type)
    {
        object? instance;
        Type optionsType;
        object? options;
        try
        {
            if (type.GetConstructor(Type.EmptyTypes) is { } parameterless)
            {
                instance = parameterless.Invoke(null);
                optionsType = type;
                options = instance;
            }
            else if (type.GetConstructors().FirstOrDefault(c => c.GetParameters() is [{ ParameterType: { IsClass: true } p }]
                         && p != typeof(string) && p.GetConstructor(Type.EmptyTypes) is not null) is { } withOptions)
            {
                optionsType = withOptions.GetParameters()[0].ParameterType;
                options = Activator.CreateInstance(optionsType);
                instance = withOptions.Invoke([options]);
            }
            else if (type.GetConstructors().FirstOrDefault(c => c.GetParameters().All(p => p.IsOptional)) is { } allOptional)
            {
                // A built-in wrapper (GZip, Zip): no markup options, its defaults stand.
                instance = allOptional.Invoke(allOptional.GetParameters().Select(p => p.DefaultValue).ToArray());
                optionsType = type;
                options = null;
            }
            else
            {
                return null;
            }
        }
        catch (Exception ex) when (ex is TargetInvocationException or MemberAccessException or MissingMethodException)
        {
            return null;
        }

        var serializer = (IMessageSerializer)instance!;
        var docs = Docs(optionsType.Assembly);
        var attributes = (options is null ? [] : optionsType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            .Where(p => p.CanWrite && p.SetMethod is { IsPublic: true } && p.GetIndexParameters().Length == 0)
            .Select(p => Attribute(p, options, docs))
            .OfType<DataFormatAttribute>()
            .OrderBy(a => a.Name, StringComparer.Ordinal)
            .ToList();
        var name = type.GetField("Name", BindingFlags.Public | BindingFlags.Static) is { IsLiteral: true } field
                   && field.FieldType == typeof(string)
            ? (string?)field.GetRawConstantValue()
            : null;
        return new DataFormatEntry(
            $"{type.FullName}, {type.Assembly.GetName().Name}",
            name,
            serializer.ContentType,
            serializer.MediaTypes.ToList(),
            attributes,
            UnmarshalTargetsByType.TryGetValue(type.FullName!, out var targets) ? targets : null);
    }

    private static DataFormatAttribute? Attribute(PropertyInfo property, object? options, IReadOnlyDictionary<string, string> docs)
    {
        var type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
        string kind;
        IReadOnlyList<string>? values = null;
        if (type == typeof(string))
            kind = IsPath(property) ? "Path" : "String";
        else if (type == typeof(bool))
            kind = "Bool";
        else if (type == typeof(int) || type == typeof(long))
            kind = "Int";
        else if (type == typeof(double) || type == typeof(float) || type == typeof(decimal))
            kind = "Number";
        else if (type == typeof(TimeSpan))
            kind = "TimeSpan";
        else if (type.IsEnum)
        {
            kind = "Enum";
            values = Enum.GetNames(type);
        }
        else
            return null; // not a value an attribute can carry

        string? defaultValue = null;
        try
        {
            defaultValue = property.GetValue(options) switch
            {
                null => null,
                bool b => b ? "true" : "false",
                IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
                var other => other.ToString(),
            };
        }
        catch (TargetInvocationException)
        {
        }

        var key = $"P:{property.DeclaringType!.FullName}.{property.Name}";
        var description = docs.TryGetValue(key, out var summary) && summary.Length > 0 ? summary : property.Name;
        return new DataFormatAttribute(char.ToLowerInvariant(property.Name[0]) + property.Name[1..], kind, values,
            defaultValue == "" ? null : defaultValue, description);
    }

    /// <summary>
    /// The Path kind, as no type says it: a name ending in File or Path, SchemaRoot, or a property the data below names
    /// (a schema that is a file for one format is inline text for another: Avro's Schema is the schema itself).
    /// </summary>
    private static bool IsPath(PropertyInfo property)
        => property.Name == "SchemaRoot"
           || property.Name.EndsWith("File", StringComparison.Ordinal) || property.Name.EndsWith("Path", StringComparison.Ordinal)
           || (PathPropertiesByType.TryGetValue(property.DeclaringType!.FullName!, out var names) && names.Contains(property.Name));

    /// <summary>String properties that name a file, beyond the naming rule.</summary>
    private static readonly Dictionary<string, string[]> PathPropertiesByType = new(StringComparer.Ordinal)
    {
        ["redb.Route.DataFormats.XmlJson.XmlJsonDataFormat"] = ["Schema"],
    };

    /// <summary>The formats the engine registers by default, by type name: they answer to their content type without a Name.</summary>
    private static readonly HashSet<string> DefaultTypes = new(StringComparer.Ordinal)
    {
        "redb.Route.Serialization.JsonMessageSerializer",
        "redb.Route.Serialization.XmlMessageSerializer",
        "redb.Route.Serialization.Base64MessageSerializer",
        "redb.Route.Serialization.GZipMessageSerializer",
        "redb.Route.Serialization.ZipMessageSerializer",
        "redb.Route.Serialization.FormUrlEncodedMessageSerializer",
    };

    private static bool HasName(Type type)
        => type.GetField("Name", BindingFlags.Public | BindingFlags.Static) is { IsLiteral: true } field && field.FieldType == typeof(string);

    private static List<string> _docDirs = [];

    private static readonly Dictionary<Assembly, IReadOnlyDictionary<string, string>> DocsCache = [];

    /// <summary>The first sentence of each member's summary, from the XML doc file beside the assembly.</summary>
    private static IReadOnlyDictionary<string, string> Docs(Assembly assembly)
    {
        if (DocsCache.TryGetValue(assembly, out var cached))
            return cached;
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var file = string.IsNullOrEmpty(assembly.Location) ? null : Path.ChangeExtension(assembly.Location, ".xml");
        if (file is null || !File.Exists(file))
            file = DocFileIn(_docDirs, assembly.GetName().Name + ".xml");
        if (file is not null && File.Exists(file))
        {
            try
            {
                foreach (var member in XDocument.Load(file).Descendants("member"))
                    if (member.Attribute("name")?.Value is { } name && member.Element("summary") is { } summary)
                        result[name] = FirstSentence(summary);
            }
            catch (System.Xml.XmlException)
            {
            }
        }
        DocsCache[assembly] = result;
        return result;
    }

    /// <summary>The newest <paramref name="fileName"/> under <paramref name="dirs"/>, searched recursively; <c>null</c> when none.</summary>
    internal static string? DocFileIn(IReadOnlyList<string> dirs, string fileName)
        => dirs.Where(Directory.Exists)
            .SelectMany(d => Directory.EnumerateFiles(d, fileName, SearchOption.AllDirectories))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();

    private static string FirstSentence(XElement summary)
    {
        var text = string.Concat(summary.Nodes().Select(node => node switch
        {
            XText t => t.Value,
            XElement { Name.LocalName: "see" or "seealso" } see => ShortName(see.Attribute("cref")?.Value ?? see.Attribute("langword")?.Value ?? see.Value),
            XElement e => e.Value,
            _ => "",
        }));
        text = Regex.Replace(text, @"\s+", " ").Trim();
        var end = Regex.Match(text, @"(?<=[.!?])\s");
        return end.Success ? text[..end.Index] : text;
    }

    private static string ShortName(string cref)
    {
        var name = cref.Contains(':') ? cref[(cref.IndexOf(':') + 1)..] : cref;
        var paren = name.IndexOf('(');
        if (paren >= 0) name = name[..paren];
        return name.Contains('.') ? name[(name.LastIndexOf('.') + 1)..] : name;
    }
}

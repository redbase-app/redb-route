using System.Xml;
using System.Xml.Linq;

namespace redb.Route.Xml.Packaging;

/// <summary>
/// The package checks of the xmljson data format that need no code of the format, so they run without <c>--bin</c>:
/// the format is recognised by its type name on <c>&lt;dataFormat type&gt;</c> and the per-step
/// <c>&lt;marshal type&gt;</c> / <c>&lt;unmarshal type&gt;</c>; its <c>arrays=</c> paths are well formed; its schema is a
/// file the package ships; a format with neither <c>schema=</c> nor <c>arrays=</c> is warned. What needs the schema
/// compiled (an <c>arrays=</c> path through the schema's structure, wildcards, mixed content, the plural pairs) is the
/// format's own <see cref="IPackageCheck"/>, which runs with <c>--bin</c>.
/// </summary>
public static partial class RoutePackage
{
    private const string XmlJsonType = "redb.Route.DataFormats.XmlJson.XmlJsonDataFormat";

    private static void CheckXmlJsonFormats(string projectDir, List<(string File, XDocument Document)> documents,
        List<PackageFinding> findings)
    {
        foreach (var (file, document) in documents)
            foreach (var element in document.Descendants())
            {
                if (element.Name.LocalName is not ("dataFormat" or "marshal" or "unmarshal"))
                    continue;
                var type = element.Attribute("type")?.Value;
                if (type is null || !string.Equals(type.Split(',')[0].Trim(), XmlJsonType, StringComparison.Ordinal))
                    continue;
                CheckXmlJson(projectDir, file, element, findings);
            }
    }

    private static void CheckXmlJson(string projectDir, string file, XElement element, List<PackageFinding> findings)
    {
        var schema = element.Attribute("schema");
        var arrays = element.Attribute("arrays");
        var paths = (arrays?.Value ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var path in paths)
            if (XmlJsonPathProblem(path) is { } problem)
                findings.Add(new PackageFinding(true, Position(file, arrays!), $"xmljson arrays path '{path}' {problem}."));

        if (schema is null)
        {
            if (paths.Length == 0)
                findings.Add(new PackageFinding(false, Position(file, element),
                    "xmljson without schema= or arrays=: a single element becomes an object and two become an array, so one " +
                    "message type gives two JSON shapes. Name the schema, or the repeated elements with arrays=."));
            return;
        }

        var value = schema.Value;
        if (value.Contains("{{", StringComparison.Ordinal) || value.Contains("${", StringComparison.Ordinal))
            return; // known only once configuration resolves it
        if (PackagedResource(projectDir, value, out var schemaProblem) is null)
            findings.Add(new PackageFinding(true, Position(file, schema), $"xmljson schema '{value}' {schemaProblem}."));
    }

    /// <summary>What is wrong with an arrays path, worded after the format's own error; <c>null</c> when it is well formed.</summary>
    private static string? XmlJsonPathProblem(string path)
    {
        var anywhere = path.StartsWith("//", StringComparison.Ordinal);
        if (!anywhere && !path.StartsWith('/'))
            return "starts with '/' (from the root) or '//' (at any depth)";
        var steps = path[(anywhere ? 2 : 1)..].Split('/');
        return steps.Any(s => s.Length == 0 || s.Any(char.IsWhiteSpace))
            ? "has an empty or blank step; write it as /Order/Items/Item or //Item"
            : null;
    }

    private static string Position(string file, XObject node)
    {
        var line = (IXmlLineInfo)node;
        return $"{file}({line.LineNumber},{line.LinePosition})";
    }
}

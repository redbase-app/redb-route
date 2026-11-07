using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;
using redb.Route.Validation;

namespace redb.Route.Xml.Packaging;

/// <summary>
/// The package check of <c>&lt;validateXsd file="…"/&gt;</c>: the schema is compiled as the route compiles it when it
/// starts (<see cref="XsdValidator.FromFile(string, string?, string?)"/>), so an import that is missing, lies outside
/// <c>schemaRoot</c> or points to the network fails at check and pack, not on the worker. A <c>schemaRoot</c> that
/// leads out of <c>resources/</c> is an error too: pack ships only that tree, so the imports there would not travel.
/// </summary>
public static partial class RoutePackage
{
    private static void CheckXsdSchemas(string projectDir, List<(string File, XDocument Document)> documents,
        List<PackageFinding> findings)
    {
        var resources = Path.GetFullPath(Path.Combine(projectDir, ResourcesDir));
        foreach (var (file, document) in documents)
            foreach (var element in document.Descendants())
                if (element.Name.LocalName == "validateXsd" && element.Attribute("file") is { } schema)
                    CheckXsdSchema(projectDir, resources, file, element, schema, findings);
    }

    private static void CheckXsdSchema(string projectDir, string resources, string file, XElement element,
        XAttribute schema, List<PackageFinding> findings)
    {
        var value = schema.Value;
        var targetNamespace = element.Attribute("targetNamespace")?.Value;
        var schemaRoot = element.Attribute("schemaRoot");
        if (value.StartsWith("assembly:", StringComparison.OrdinalIgnoreCase)
            || HoldsPlaceholder(value) || HoldsPlaceholder(targetNamespace) || HoldsPlaceholder(schemaRoot?.Value))
            return; // known only once configuration resolves it
        if (PackagedResource(projectDir, value, out _) is not { } path)
            return; // the resource check reports a schema the package does not ship

        if (schemaRoot is not null)
        {
            var root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, schemaRoot.Value));
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!string.Equals(root, resources, comparison)
                && !root.StartsWith(resources + Path.DirectorySeparatorChar, comparison))
            {
                findings.Add(new PackageFinding(true, Position(file, schemaRoot),
                    $"validateXsd schemaRoot='{schemaRoot.Value}' of schema '{value}' leads outside 'resources/': pack ships " +
                    "only 'resources/', so the imports there would not reach the worker. Keep the schema and its imports under 'resources/'."));
                return;
            }
        }

        try
        {
            XsdValidator.FromFile(path, targetNamespace, schemaRoot?.Value);
        }
        catch (Exception ex) when (ex is XmlSchemaException or XmlException or IOException or UnauthorizedAccessException)
        {
            findings.Add(new PackageFinding(true, Position(file, schema), $"validateXsd schema '{value}' does not compile: {ex.Message}"));
        }
    }

    private static bool HoldsPlaceholder(string? value)
        => value is not null && (value.Contains("{{", StringComparison.Ordinal) || value.Contains("${", StringComparison.Ordinal));
}

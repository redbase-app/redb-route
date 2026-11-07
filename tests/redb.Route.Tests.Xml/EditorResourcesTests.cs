using System.Text.Json;
using System.Xml.Linq;
using AwesomeAssertions;
using redb.Route.Xml;

namespace redb.Route.Tests.Xml;

/// <summary>
/// The VS Code extension reads four generated resources: media/redb-route-1.0.catalog.xsd (the schema
/// catalog.xml binds the namespace to, so it is the one the editor validates a document against),
/// media/redb-route-1.0.xsd, media/redb-route-elements.json (palette and property panels) and media/redb-route-dataformats.json (formats on &lt;dataFormat&gt; and &lt;marshal type&gt;). Regenerating
/// them is a hand step at release time (RELEASE_ARTIFACTS.md, «VS Code extension»), and nothing noticed when
/// it was skipped: <c>&lt;route tracing="false"&gt;</c> reached the engine in 4.2.2, the editor went on
/// refusing the attribute, and logHandled/logContinued were about to repeat it (2026-10-08).
///
/// The resources are generated WITH the package contributions, so the committed files carry more than the
/// core list of this test project: what is pinned is that every core element and every core attribute is
/// among them, not equality of the files.
/// </summary>
public class EditorResourcesTests
{
    private static readonly ElementRegistry Registry = ElementRegistry.CreateDefault();

    private static string MediaDir { get; } = Locate();

    private static string Locate()
    {
        var probed = new List<string>();
        for (var current = new DirectoryInfo(AppContext.BaseDirectory); current is not null; current = current.Parent)
        {
            var candidate = Path.Combine(current.FullName, "redb.Route", "tools", "vscode-redb-route", "media");
            probed.Add(candidate);
            if (Directory.Exists(candidate))
                return candidate;
        }
        throw new DirectoryNotFoundException(
            "The extension's media directory was not found above the test output. Probed: " + string.Join("; ", probed));
    }

    /// <summary>Every attribute a schema declares per element name (a nested element's attributes count to
    /// its parent too — a superset is harmless for the check below).</summary>
    private static Dictionary<string, HashSet<string>> DeclaredAttributes(string schemaPath)
    {
        var document = XDocument.Load(schemaPath);
        var xs = document.Root!.Name.Namespace;
        var declared = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        // An element declared with type="r:name" (the step <bean>, type beanStep) carries its attributes in the
        // named complex type, not below itself.
        var namedTypes = document.Root.Elements(xs + "complexType")
            .Where(t => t.Attribute("name") is not null)
            .ToDictionary(t => (string)t.Attribute("name")!, StringComparer.Ordinal);
        foreach (var element in document.Descendants(xs + "element"))
        {
            if ((string?)element.Attribute("name") is not { } name)
                continue;
            if (!declared.TryGetValue(name, out var attributes))
                declared[name] = attributes = new HashSet<string>(StringComparer.Ordinal);
            var sources = new List<XElement> { element };
            if ((string?)element.Attribute("type") is { } typeName
                && namedTypes.TryGetValue(typeName[(typeName.IndexOf(':') + 1)..], out var namedType))
                sources.Add(namedType);
            foreach (var attribute in sources.SelectMany(s => s.Descendants(xs + "attribute")))
                if ((string?)attribute.Attribute("name") is { } attributeName)
                    attributes.Add(attributeName);
        }
        return declared;
    }

    private static List<string> MissingCoreAttributes(Dictionary<string, HashSet<string>> declared)
    {
        var missing = new List<string>();
        foreach (var contribution in Registry.Contributions)
        {
            var spec = contribution.Spec;
            foreach (var attribute in spec.Attributes)
                if (!declared.TryGetValue(spec.Name, out var attributes) || !attributes.Contains(attribute.Name))
                    missing.Add($"<{spec.Name} {attribute.Name}=\"...\">");
        }
        return missing;
    }

    [Fact]
    public void CatalogSchema_DeclaresEveryCoreAttribute()
    {
        // The schema the editor actually validates against: an attribute missing here is underlined in every
        // document that uses it, however well the engine accepts it.
        var path = Path.Combine(MediaDir, "redb-route-1.0.catalog.xsd");
        MissingCoreAttributes(DeclaredAttributes(path)).Should().BeEmpty(
            $"{Path.GetFileName(path)} is regenerated from the element list — " +
            "run the three redb-route-xml commands of RELEASE_ARTIFACTS.md (VS Code extension) and commit the result");
    }

    [Fact]
    public void LightweightSchema_DeclaresEveryCoreAttribute()
    {
        var path = Path.Combine(MediaDir, "redb-route-1.0.xsd");
        MissingCoreAttributes(DeclaredAttributes(path)).Should().BeEmpty(
            $"{Path.GetFileName(path)} is regenerated together with the catalog schema");
    }

    [Fact]
    public void CatalogSchema_Compiles()
    {
        // The file the editor binds through catalog.xml: a schema that does not compile is no schema at all,
        // and the editor reports the damage on every document rather than at its own load (2026-09-20, a
        // component whose path synonym was also an option declared one attribute twice).
        var path = Path.Combine(MediaDir, "redb-route-1.0.catalog.xsd");
        var errors = new List<string>();
        var set = new System.Xml.Schema.XmlSchemaSet();
        set.ValidationEventHandler += (_, e) => errors.Add(e.Message);
        set.Add("urn:redb:route:1.0", System.Xml.XmlReader.Create(path));
        set.Compile();

        errors.Should().BeEmpty($"{Path.GetFileName(path)} must compile against the namespace catalog.xml binds");
    }

    [Fact]
    public void ElementsJson_ListsEveryCoreElementAndItsAttributes()
    {
        var path = Path.Combine(MediaDir, "redb-route-elements.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var declared = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var element in document.RootElement.EnumerateArray())
        {
            var name = element.GetProperty("name").GetString()!;
            declared[name] = element.GetProperty("attributes").EnumerateArray()
                .Select(attribute => attribute.GetProperty("name").GetString()!)
                .ToHashSet(StringComparer.Ordinal);
        }

        MissingCoreAttributes(declared).Should().BeEmpty(
            $"{Path.GetFileName(path)} drives the palette and the property panel, so an element or an " +
            "attribute missing from it is one the editor cannot offer");
    }

    /// <summary>
    /// The data formats this test project carries, with the markup attributes their type binds (the serializer's own
    /// writable properties with a parameterless constructor, else its options object's), in the value types an
    /// attribute can carry. The committed catalog must list each, by name and type, with every such attribute.
    /// </summary>
    [Theory]
    [InlineData(typeof(redb.Route.DataFormats.XmlJson.XmlJsonDataFormat), "xmljson")]
    [InlineData(typeof(redb.Route.DataFormats.Mime.MimeMultipartDataFormat), "mimeMultipart")]
    public void DataFormatsJson_ListsEveryFormatAndItsAttributes(Type format, string name)
    {
        var path = Path.Combine(MediaDir, "redb-route-dataformats.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var typeName = $"{format.FullName}, {format.Assembly.GetName().Name}";
        var entry = document.RootElement.EnumerateArray()
            .FirstOrDefault(e => e.GetProperty("type").GetString() == typeName);

        entry.ValueKind.Should().Be(JsonValueKind.Object,
            $"{Path.GetFileName(path)} is regenerated with the data format bins; {typeName} is missing from it");
        entry.GetProperty("name").GetString().Should().Be(name);
        var listed = entry.GetProperty("attributes").EnumerateArray().Select(a => a.GetProperty("name").GetString()!).ToHashSet();
        MarkupAttributes(format).Except(listed).Should().BeEmpty(
            $"{Path.GetFileName(path)} drives completion on <dataFormat> and <marshal type>; regenerate it");
    }

    private static IEnumerable<string> MarkupAttributes(Type format)
    {
        var target = format.GetConstructor(Type.EmptyTypes) is not null
            ? format
            : format.GetConstructors().Select(c => c.GetParameters()).First(p => p.Length == 1)[0].ParameterType;
        Type[] carried = [typeof(string), typeof(bool), typeof(int), typeof(long), typeof(double), typeof(float), typeof(decimal), typeof(TimeSpan)];
        return target.GetProperties()
            .Where(p => p.SetMethod is { IsPublic: true })
            .Where(p => (Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType) is var t && (t.IsEnum || carried.Contains(t)))
            .Select(p => char.ToLowerInvariant(p.Name[0]) + p.Name[1..]);
    }
}

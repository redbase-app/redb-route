using System.Runtime.Loader;
using AwesomeAssertions;
using redb.Route.Xml.CodeGen;

namespace redb.Route.Tests.Xml;

/// <summary>
/// The generator's data format catalog (redb-route-dataformats.json): formats found in a bin with their name, content
/// types, markup attributes by the CreateSerializer rule, kinds (Path by rule and data, Enum with values), defaults,
/// descriptions from the XML docs, and the unmarshal targets where a format has a fixed set.
/// </summary>
public sealed class DataFormatCatalogTests
{
    private static readonly List<DataFormatEntry> Catalog = DataFormatCatalog.Load(
        [AppContext.BaseDirectory],
        name => name is null ? null : AssemblyLoadContext.Default.Assemblies.FirstOrDefault(a => a.GetName().Name == name));

    private static DataFormatEntry Entry(string name) => Catalog.Single(e => e.Name == name);

    [Fact]
    public void Xmljson_ItsAttributesKindsAndTargets()
    {
        var xmljson = Entry("xmljson");

        xmljson.Type.Should().Be("redb.Route.DataFormats.XmlJson.XmlJsonDataFormat, redb.Route.DataFormats.XmlJson");
        xmljson.ContentType.Should().Be("application/x-xmljson");
        xmljson.Attributes.Single(a => a.Name == "schema").Kind.Should().Be("Path");
        xmljson.Attributes.Single(a => a.Name == "schemaRoot").Kind.Should().Be("Path");
        xmljson.Attributes.Single(a => a.Name == "arrays").Kind.Should().Be("String");
        var namespaces = xmljson.Attributes.Single(a => a.Name == "namespaces");
        namespaces.Kind.Should().Be("Enum");
        namespaces.EnumValues.Should().Equal("Keep", "Drop");
        namespaces.Default.Should().Be("Keep");
        xmljson.Attributes.Single(a => a.Name == "textKey").Default.Should().Be("#text");
        xmljson.Attributes.Should().OnlyContain(a => a.Description.Length > 0);
        xmljson.UnmarshalTargets.Should().Contain("System.Text.Json.Nodes.JsonNode, System.Text.Json");
    }

    [Fact]
    public void Mime_TakesItsOptionsObjectsProperties()
    {
        var mime = Entry("mimeMultipart");

        mime.Attributes.Select(a => a.Name).Should().Contain(["maxParts", "subType"]);
        mime.UnmarshalTargets.Should().BeNull("a format without a fixed set of targets takes any type");
    }

    [Fact]
    public void TheCoreFormats_AreListedWithoutAName()
    {
        Catalog.Should().Contain(e => e.Type.StartsWith("redb.Route.Serialization.JsonMessageSerializer,", StringComparison.Ordinal) && e.Name == null);
        Catalog.Should().Contain(e => e.Type.StartsWith("redb.Route.Serialization.GZipMessageSerializer,", StringComparison.Ordinal),
            "a built-in wrapper with only optional constructor parameters is a format too");
    }

    [Fact]
    public void ASerializerWithoutANameOrADefaultRegistration_IsNotADataFormat()
    {
        // ProbeDataFormat (this test assembly) is an IMessageSerializer, as Telegram's update reader is: neither names a format.
        Catalog.Should().NotContain(e => e.Type.StartsWith(typeof(ProbeDataFormat).FullName + ",", StringComparison.Ordinal));
    }

    [Fact]
    public void ADocFile_IsFoundInTheDocDirs_TheNewestFirst()
    {
        var root = Path.Combine(Path.GetTempPath(), "redb-docs-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "old"));
            Directory.CreateDirectory(Path.Combine(root, "new", "bin"));
            var older = Path.Combine(root, "old", "Pkg.xml");
            var newer = Path.Combine(root, "new", "bin", "Pkg.xml");
            File.WriteAllText(older, "<doc/>");
            File.WriteAllText(newer, "<doc/>");
            File.SetLastWriteTimeUtc(older, DateTime.UtcNow.AddDays(-1));

            DataFormatCatalog.DocFileIn([root], "Pkg.xml").Should().Be(newer);
            DataFormatCatalog.DocFileIn([root], "None.xml").Should().BeNull();
            DataFormatCatalog.DocFileIn([Path.Combine(root, "missing")], "Pkg.xml").Should().BeNull();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

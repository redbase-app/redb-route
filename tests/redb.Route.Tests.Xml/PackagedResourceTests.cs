using AwesomeAssertions;
using redb.Route.Xml.Packaging;

namespace redb.Route.Tests.Xml;

/// <summary>
/// The gate holds a resource reference to what Pack ships, the <c>resources/</c> tree (findings register row 42). A file
/// beside the project (next to the .csproj) used to pass check, since local runs find it, and the worker failed at
/// run time because the package never carried it.
/// </summary>
public sealed class PackagedResourceTests : IDisposable
{
    private const string XmlJsonType = "redb.Route.DataFormats.XmlJson.XmlJsonDataFormat, redb.Route.DataFormats.XmlJson";
    private const string Xsd = """<xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema"><xs:element name="a" type="xs:string"/></xs:schema>""";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "redb-xml-res-" + Guid.NewGuid().ToString("N"));

    public PackagedResourceTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private PackageResult Check(string step, string relativePath)
    {
        var dir = RouteProjectScaffold.Create(_root, "Res" + Guid.NewGuid().ToString("N")[..6]);
        var full = Path.Combine(dir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, Xsd);
        File.WriteAllText(Path.Combine(dir, "routes", "main.route.xml"), $"""
            <routes xmlns="urn:redb:route:1.0">
              <route id="r">
                <from uri="direct://in"/>
            {step}
              </route>
            </routes>
            """);
        return RoutePackage.Check(dir, "p", "1.0.0");
    }

    [Theory]
    [InlineData("""<validateXsd file="order.xsd"/>""", "resources/order.xsd")]
    [InlineData("""<validateXsd file="resources/order.xsd"/>""", "resources/order.xsd")]
    [InlineData("""<validateXsd file="schemas/order.xsd"/>""", "resources/schemas/order.xsd")]
    public void AResourceUnderResources_Passes(string step, string file)
    {
        Check(step, file).Errors.Should().NotContain(e => e.Message.Contains("resource '"));
    }

    [Fact]
    public void AResourceBesideTheProject_IsAnError_ThatSaysToMoveIt()
    {
        Check("""<validateXsd file="order.xsd"/>""", "order.xsd").Errors.Should().Contain(e =>
            e.Message == "resource 'order.xsd' referenced by <validateXsd file=…> is at 'order.xsd', outside 'resources/': pack ships only 'resources/', so the worker would not find it. Move it under 'resources/'."
            && e.File.Contains("main.route.xml("));
    }

    [Fact]
    public void AMissingResource_IsTheUsualError()
    {
        Check("""<validateXsd file="none.xsd"/>""", "resources/order.xsd").Errors
            .Should().Contain(e => e.Message == "resource 'none.xsd' referenced by <validateXsd file=…> is not under 'resources/'.");
    }

    [Fact]
    public void AnXmlJsonSchemaBesideTheProject_IsAnError()
    {
        Check($"""<marshal type="{XmlJsonType}" schema="order.xsd"/>""", "order.xsd").Errors
            .Should().Contain(e => e.Message.StartsWith("xmljson schema 'order.xsd' is at 'order.xsd', outside 'resources/': pack ships only 'resources/'", StringComparison.Ordinal));
    }

    [Fact]
    public void AnXmlJsonSchemaUnderResources_Passes()
    {
        Check($"""<marshal type="{XmlJsonType}" schema="order.xsd"/>""", "resources/order.xsd").Errors
            .Should().NotContain(e => e.Message.Contains("xmljson schema"));
    }
}

using AwesomeAssertions;
using redb.Route.Xml.Packaging;

namespace redb.Route.Tests.Xml;

/// <summary>
/// Row 37 step f: check and pack compile the schema of <c>&lt;validateXsd file="…"/&gt;</c> as the route does when it
/// starts, so an import that is missing, outside <c>schemaRoot</c> or on the network fails at the gate, not on the
/// worker; a <c>schemaRoot</c> leading out of <c>resources/</c> is an error, since pack ships only that tree.
/// </summary>
public sealed class XsdPackageGateTests : IDisposable
{
    private const string Common = """
        <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" targetNamespace="urn:common" elementFormDefault="qualified">
          <xs:simpleType name="Code"><xs:restriction base="xs:string"/></xs:simpleType>
        </xs:schema>
        """;

    private static string Order(string importLocation) => $"""
        <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" xmlns:c="urn:common" targetNamespace="urn:order" elementFormDefault="qualified">
          <xs:import namespace="urn:common" schemaLocation="{importLocation}"/>
          <xs:element name="order"><xs:complexType><xs:sequence><xs:element name="currency" type="c:Code"/></xs:sequence></xs:complexType></xs:element>
        </xs:schema>
        """;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "redb-xml-xsdgate-" + Guid.NewGuid().ToString("N"));

    public XsdPackageGateTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private PackageResult Check(string step, params (string Path, string Content)[] files)
    {
        var dir = RouteProjectScaffold.Create(_root, "Xsd" + Guid.NewGuid().ToString("N")[..6]);
        foreach (var (path, content) in files)
        {
            var full = Path.Combine(dir, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }
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

    private static IEnumerable<string> XsdErrors(PackageResult result)
        => result.Errors.Where(e => e.Message.StartsWith("validateXsd ", StringComparison.Ordinal)).Select(e => e.Message);

    [Fact]
    public void ASchemaWithItsImportsUnderResources_Passes()
    {
        var result = Check("""<validateXsd file="schemas/order/order.xsd" schemaRoot=".."/>""",
            ("resources/schemas/order/order.xsd", Order("../common/common.xsd")),
            ("resources/schemas/common/common.xsd", Common));

        XsdErrors(result).Should().BeEmpty();
    }

    [Fact]
    public void AnImportOutsideSchemaRoot_FailsTheGate()
    {
        var result = Check("""<validateXsd file="schemas/order/order.xsd"/>""",
            ("resources/schemas/order/order.xsd", Order("../common/common.xsd")),
            ("resources/schemas/common/common.xsd", Common));

        result.Errors.Should().ContainSingle(e => e.Message.StartsWith("validateXsd schema 'schemas/order/order.xsd' does not compile: validateXsd: the schema refers to ", StringComparison.Ordinal)
                                                  && e.Message.EndsWith("imports and includes stay inside the package.", StringComparison.Ordinal)
                                                  && e.File.Contains("main.route.xml("));
    }

    [Fact]
    public void AMissingImport_FailsTheGate()
    {
        var result = Check("""<validateXsd file="order.xsd"/>""", ("resources/order.xsd", Order("common.xsd")));

        XsdErrors(result).Should().ContainSingle().Which.Should().StartWith("validateXsd schema 'order.xsd' does not compile: validateXsd: schema '");
    }

    [Fact]
    public void ANetworkImport_FailsTheGate()
    {
        var result = Check("""<validateXsd file="order.xsd"/>""", ("resources/order.xsd", Order("http://127.0.0.1:9/common.xsd")));

        XsdErrors(result).Should().ContainSingle().Which.Should().Contain("not the network");
    }

    [Fact]
    public void ASchemaRootOutOfResources_IsAnError()
    {
        var result = Check("""<validateXsd file="order.xsd" schemaRoot=".."/>""",
            ("resources/order.xsd", Order("common.xsd")), ("resources/common.xsd", Common));

        XsdErrors(result).Should().ContainSingle().Which.Should().Be(
            "validateXsd schemaRoot='..' of schema 'order.xsd' leads outside 'resources/': pack ships only 'resources/', so the imports there would not reach the worker. Keep the schema and its imports under 'resources/'.");
    }

    [Fact]
    public void ASchemaThatDoesNotCompile_FailsTheGate()
    {
        var result = Check("""<validateXsd file="bad.xsd"/>""",
            ("resources/bad.xsd", """<xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema"><xs:element name="a" type="nope"/></xs:schema>"""));

        XsdErrors(result).Should().ContainSingle().Which.Should().StartWith("validateXsd schema 'bad.xsd' does not compile: ");
    }

    [Theory]
    [InlineData("""<validateXsd file="{{schemas.order}}"/>""")]
    [InlineData("""<validateXsd file="order.xsd" schemaRoot="{{schemas.root}}"/>""")]
    public void AValueKnownOnlyFromConfiguration_IsLeftAlone(string step)
    {
        var result = Check(step, ("resources/order.xsd", Order("common.xsd")));

        XsdErrors(result).Should().BeEmpty();
    }

    [Fact]
    public void InlineContent_IsNotCompiledByTheGate()
    {
        var result = Check("""<validateXsd><![CDATA[<xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema"/>]]></validateXsd>""");

        XsdErrors(result).Should().BeEmpty();
    }
}

using AwesomeAssertions;
using redb.Route.DataFormats.XmlJson;
using redb.Route.Xml.Packaging;

namespace redb.Route.Tests.Xml;

/// <summary>
/// The package gate on the xmljson format. Without <c>--bin</c> the gate's own checks run, recognising the format by its
/// type name: the schema is a file the package ships, the arrays paths are well formed, a format with neither schema
/// nor arrays is warned. With <c>--bin</c> the format's <see cref="XmlJsonPackageCheck"/> runs as well: the schema
/// compiles with the format's reader, each arrays path leads through the schema's structure, xs:any and mixed content
/// are flagged, and plural=true names the pairs it reads as lists.
/// </summary>
public sealed class XmlJsonGateTests : IDisposable
{
    private const string Type = "redb.Route.DataFormats.XmlJson.XmlJsonDataFormat, redb.Route.DataFormats.XmlJson";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "redb-xml-xmljson-" + Guid.NewGuid().ToString("N"));

    public XmlJsonGateTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private const string OrderXsd = """
        <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema">
          <xs:element name="Order"><xs:complexType><xs:sequence>
            <xs:element name="Item" type="xs:string" maxOccurs="unbounded"/>
            <xs:element name="Lines"><xs:complexType><xs:sequence>
              <xs:element name="Line" type="xs:string"/>
            </xs:sequence></xs:complexType></xs:element>
            <xs:element name="Extra" minOccurs="0"><xs:complexType mixed="true"><xs:sequence>
              <xs:any processContents="skip" minOccurs="0" maxOccurs="unbounded"/>
            </xs:sequence></xs:complexType></xs:element>
          </xs:sequence></xs:complexType></xs:element>
        </xs:schema>
        """;

    private PackageResult Check(string step, bool bin, params (string Path, string Content)[] resources)
    {
        var dir = RouteProjectScaffold.Create(_root, "XmlJson" + Guid.NewGuid().ToString("N")[..6]);
        foreach (var (path, content) in resources)
        {
            var full = Path.Combine(dir, RoutePackage.ResourcesDir, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }
        File.WriteAllText(Path.Combine(dir, "routes", "main.route.xml"), $"""
            <routes xmlns="urn:redb:route:1.0">
              <route id="xj">
                <from uri="direct://in"/>
            {step}
              </route>
            </routes>
            """);
        return RoutePackage.Check(dir, "p", "1.0.0", checks: bin ? [new XmlJsonPackageCheck()] : null);
    }

    // ── without --bin: the gate's own checks ────────────────────────

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AMissingSchema_IsOneError(bool bin)
    {
        Check($"""<marshal type="{Type}" schema="schemas/none.xsd"/>""", bin).Errors
            .Should().ContainSingle(e => e.Message.Contains("xmljson schema"))
            .Which.Message.Should().Be("xmljson schema 'schemas/none.xsd' is not under 'resources/'.");
    }

    [Theory]
    [InlineData("Item", "starts with '/' (from the root) or '//' (at any depth)")]
    [InlineData("/Order//Item", "has an empty or blank step; write it as /Order/Items/Item or //Item")]
    public void AMalformedArraysPath_IsAnError(string path, string problem)
    {
        Check($"""<marshal type="{Type}" arrays="{path}"/>""", bin: false).Errors
            .Should().Contain(e => e.Message == $"xmljson arrays path '{path}' {problem}.");
    }

    [Fact]
    public void NeitherSchemaNorArrays_IsWarned()
    {
        Check($"""<marshal type="{Type}"/>""", bin: false).Warnings
            .Should().Contain(w => w.Message.StartsWith("xmljson without schema= or arrays=", StringComparison.Ordinal));
        Check($"""<marshal type="{Type}" arrays="//Item"/>""", bin: false).Warnings
            .Should().NotContain(w => w.Message.StartsWith("xmljson without schema= or arrays=", StringComparison.Ordinal));
    }

    [Fact]
    public void WithoutBin_TheSchemaIsNotCompiled()
    {
        var result = Check($"""<marshal type="{Type}" schema="schemas/bad.xsd" arrays="//Nothing"/>""", bin: false,
            ("schemas/bad.xsd", """<xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema"><xs:element name="a" type="nothing"/></xs:schema>"""));

        result.Errors.Should().BeEmpty("compiling the schema is the format's own check, which needs --bin");
        result.Warnings.Should().NotContain(w => w.Message.Contains("schema 'schemas/bad.xsd'"));
    }

    [Fact]
    public void TheContextLevelDataFormat_IsCheckedToo()
    {
        var dir = RouteProjectScaffold.Create(_root, "XmlJsonCtx");
        File.WriteAllText(Path.Combine(dir, RoutePackage.ContextFile), $"""
            <context xmlns="urn:redb:route:1.0">
              <dataFormat name="orders-json" type="{Type}" schema="schemas/order.xsd" arrays="/Order/Itme"/>
            </context>
            """);
        Directory.CreateDirectory(Path.Combine(dir, RoutePackage.ResourcesDir, "schemas"));
        File.WriteAllText(Path.Combine(dir, RoutePackage.ResourcesDir, "schemas", "order.xsd"), OrderXsd);

        RoutePackage.Check(dir, "p", "1.0.0", checks: [new XmlJsonPackageCheck()]).Warnings
            .Should().Contain(w => w.Message.Contains("'Itme' is not a child of 'Order'") && w.File.Contains("context.xml("));
    }

    // ── with --bin: the format's own check ──────────────────────────

    [Theory]
    [InlineData("""<xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema"><xs:element name="a" type="nothing"/></xs:schema>""", "nothing")]
    [InlineData("""<xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema"><xs:import namespace="urn:t" schemaLocation="http://example.invalid/t.xsd"/></xs:schema>""", "not the network")]
    [InlineData("""<xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema"><xs:import namespace="urn:t" schemaLocation="../../outside.xsd"/></xs:schema>""", "outside")]
    public void ASchemaThatDoesNotCompile_IsAnError(string xsd, string detail)
    {
        Check($"""<marshal type="{Type}" schema="schemas/bad.xsd"/>""", bin: true, ("schemas/bad.xsd", xsd)).Errors
            .Should().Contain(e => e.Message.StartsWith("xmljson schema 'schemas/bad.xsd' does not compile: ", StringComparison.Ordinal)
                                   && e.Message.Contains(detail) && e.File.Contains("main.route.xml("));
    }

    [Fact]
    public void ASchemaWithItsOwnDoctype_Compiles()
    {
        const string withDoctype = """
            <?xml version="1.0"?>
            <!DOCTYPE schema [<!ENTITY unused 'x'>]>
            <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema"><xs:element name="Item" type="xs:string"/></xs:schema>
            """;

        Check($"""<marshal type="{Type}" schema="schemas/d.xsd"/>""", bin: true, ("schemas/d.xsd", withDoctype)).Errors.Should().BeEmpty();
    }

    [Theory]
    [InlineData("/Order/Item")]
    [InlineData("//Item")]
    [InlineData("//Lines/Line")]
    [InlineData("/Order/Lines/Line")]
    [InlineData("/o:Order/o:Item")]
    [InlineData("/Order/Extra/Anything/Deeper")]
    public void APathThroughTheSchema_Passes(string path)
    {
        var result = Check($"""<marshal type="{Type}" schema="schemas/order.xsd" arrays="{path}"/>""", bin: true, ("schemas/order.xsd", OrderXsd));

        result.Errors.Should().BeEmpty();
        result.Warnings.Should().NotContain(w => w.Message.Contains("does not lead through"));
    }

    [Theory]
    [InlineData("/Order/Line", "'Line' is not a child of 'Order'")]
    [InlineData("/Item", "'Item' is not a global element of it")]
    [InlineData("//Itme", "'Itme' names no element of it")]
    [InlineData("//Item/Line", "'Line' is not a child of 'Item'")]
    public void APathTheSchemaHasNot_IsWarned_WithTheStepWhereItBreaks(string path, string problem)
    {
        Check($"""<marshal type="{Type}" schema="schemas/order.xsd" arrays="{path}"/>""", bin: true, ("schemas/order.xsd", OrderXsd)).Warnings
            .Should().Contain(w => w.Message == $"xmljson arrays path '{path}' does not lead through schema 'schemas/order.xsd': {problem}.");
    }

    [Fact]
    public void WildcardsAndMixedContent_AreFlagged()
    {
        var warnings = Check($"""<marshal type="{Type}" schema="schemas/order.xsd" arrays="//Item"/>""", bin: true, ("schemas/order.xsd", OrderXsd)).Warnings;

        warnings.Should().Contain(w => w.Message.Contains("has xs:any in 'Extra'"));
        warnings.Should().Contain(w => w.Message.Contains("has mixed content in 'Extra'"));
    }

    [Fact]
    public void ASchemaWithoutWildcards_IsNotFlagged_EvenWithAnUntypedElement()
    {
        // An element without a type is xs:anyType, a built-in with a wildcard; the author wrote no xs:any.
        const string xsd = """
            <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema">
              <xs:element name="Order"><xs:complexType><xs:sequence>
                <xs:element name="Item" type="xs:string" maxOccurs="unbounded"/>
                <xs:element name="Note"/>
              </xs:sequence></xs:complexType></xs:element>
            </xs:schema>
            """;

        var warnings = Check($"""<marshal type="{Type}" schema="schemas/plain.xsd" arrays="//Item"/>""", bin: true, ("schemas/plain.xsd", xsd)).Warnings;

        warnings.Should().NotContain(w => w.Message.Contains("xs:any") || w.Message.Contains("mixed content"));
    }

    [Fact]
    public void PluralTrue_NamesThePairsItReadsAsLists()
    {
        const string xsd = """
            <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema">
              <xs:element name="Report"><xs:complexType><xs:sequence>
                <xs:element name="Incidents"><xs:complexType><xs:sequence>
                  <xs:element name="Incident" type="xs:string" maxOccurs="unbounded"/>
                </xs:sequence></xs:complexType></xs:element>
                <xs:element name="Categories"><xs:complexType><xs:sequence>
                  <xs:element name="Category" type="xs:string"/>
                </xs:sequence></xs:complexType></xs:element>
                <xs:element name="Status" type="xs:string"/>
              </xs:sequence></xs:complexType></xs:element>
            </xs:schema>
            """;

        Check($"""<marshal type="{Type}" schema="schemas/r.xsd" plural="true"/>""", bin: true, ("schemas/r.xsd", xsd)).Warnings
            .Should().Contain(w => w.Message ==
                "xmljson plural=true reads 2 parent/child pair(s) of schema 'schemas/r.xsd' as lists: 'Categories/Category', " +
                "'Incidents/Incident'; the schema allows one child only in 'Categories/Category'.");
    }

    [Fact]
    public void PluralTrue_WithNoPair_SaysTheRuleChangesNothing()
    {
        const string xsd = """
            <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema">
              <xs:element name="Order"><xs:complexType><xs:sequence>
                <xs:element name="Item" type="xs:string" maxOccurs="unbounded"/>
              </xs:sequence></xs:complexType></xs:element>
            </xs:schema>
            """;

        Check($"""<marshal type="{Type}" schema="schemas/o.xsd" plural="true"/>""", bin: true, ("schemas/o.xsd", xsd)).Warnings
            .Should().Contain(w => w.Message.StartsWith("xmljson plural=true finds no parent named as the plural of its only child", StringComparison.Ordinal));
    }

    [Fact]
    public void PluralOff_ListsNothing()
    {
        Check($"""<marshal type="{Type}" schema="schemas/order.xsd" arrays="//Item"/>""", bin: true, ("schemas/order.xsd", OrderXsd)).Warnings
            .Should().NotContain(w => w.Message.Contains("plural=true"));
    }

    [Fact]
    public void TheUblInvoiceSchema_PassesWithItsRealPaths_AndAWrongStepIsNamed()
    {
        var dir = RouteProjectScaffold.Create(_root, "Ubl");
        var source = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..",
            "redb.Route.Tests.DataFormats", "Fixtures", "xmljson", "ubl"));
        foreach (var file in Directory.GetFiles(source, "*.xsd", SearchOption.AllDirectories))
        {
            var target = Path.Combine(dir, RoutePackage.ResourcesDir, "ubl", Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
        File.WriteAllText(Path.Combine(dir, "routes", "main.route.xml"), $"""
            <routes xmlns="urn:redb:route:1.0">
              <route id="ubl"><from uri="direct://in"/>
                <marshal type="{Type}" schema="ubl/maindoc/UBL-Invoice-2.1.xsd" schemaRoot=".."
                         arrays="/Invoice/cac:InvoiceLine,//cac:InvoiceLine/cbc:Note,/Invoice/cac:InvoiceLine/cbc:Nope"/>
              </route>
            </routes>
            """);

        var result = RoutePackage.Check(dir, "p", "1.0.0", checks: [new XmlJsonPackageCheck()]);

        result.Errors.Should().BeEmpty();
        result.Warnings.Where(w => w.Message.Contains("does not lead through")).Should().ContainSingle()
            .Which.Message.Should().EndWith(": 'cbc:Nope' is not a child of 'cac:InvoiceLine'.");
    }

    // ── the hook itself ─────────────────────────────────────────────

    private sealed class Throwing : IPackageCheck
    {
        public void Check(PackageCheckContext context) => throw new InvalidOperationException("boom");
    }

    [Fact]
    public void ACheckThatThrows_IsAnErrorNamingIt_AndTheOthersStillRun()
    {
        var dir = RouteProjectScaffold.Create(_root, "Hook");
        Directory.CreateDirectory(Path.Combine(dir, RoutePackage.ResourcesDir, "schemas"));
        File.WriteAllText(Path.Combine(dir, RoutePackage.ResourcesDir, "schemas", "order.xsd"), OrderXsd);
        File.WriteAllText(Path.Combine(dir, "routes", "main.route.xml"), $"""
            <routes xmlns="urn:redb:route:1.0">
              <route id="xj"><from uri="direct://in"/><marshal type="{Type}" schema="schemas/order.xsd" arrays="/Order/Line"/></route>
            </routes>
            """);

        var result = RoutePackage.Check(dir, "p", "1.0.0", checks: [new Throwing(), new XmlJsonPackageCheck()]);

        result.Errors.Should().Contain(e => e.File.EndsWith("+Throwing", StringComparison.Ordinal)
                                            && e.Message == "the package check failed: InvalidOperationException: boom");
        result.Warnings.Should().Contain(w => w.Message.Contains("'Line' is not a child of 'Order'"));
    }
}

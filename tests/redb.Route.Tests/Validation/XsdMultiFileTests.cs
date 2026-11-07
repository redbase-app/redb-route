using System.Xml.Schema;
using AwesomeAssertions;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Validation;
using Xunit;

namespace redb.Route.Tests.Validation;

/// <summary>
/// Row 37: an XSD read from its file resolves its <c>xs:import</c> and <c>xs:include</c> relative to it, from under
/// one root only. A reference that is not found, lies outside the root or points to the network is an error naming it;
/// so is anything else the schema compiler reports. Before, every form read the file as text and lost its location,
/// so multi-file schemas (UBL, ebMS3) could not be validated at all.
/// </summary>
public sealed class XsdMultiFileTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "xsd-multi-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly RouteContext _context = new();

    private static string Fixtures => Path.Combine(AppContext.BaseDirectory, "Fixtures", "xsd");

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _context.DisposeAsync();
        Directory.Delete(_dir, recursive: true);
    }

    private string Write(string relative, string content)
    {
        var path = Path.Combine(_dir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private static IExchange Body(string xml) => new Exchange(new Message(xml));

    private const string Common = """
        <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" targetNamespace="urn:common" xmlns="urn:common" elementFormDefault="qualified">
          <xs:simpleType name="Code"><xs:restriction base="xs:string"><xs:pattern value="[A-Z]{3}"/></xs:restriction></xs:simpleType>
          <xs:element name="note" type="xs:string"/>
          <xs:element name="amount" type="xs:int"/>
        </xs:schema>
        """;

    private const string Part = """
        <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" targetNamespace="urn:order" xmlns="urn:order" elementFormDefault="qualified">
          <xs:element name="id" type="xs:string"/>
        </xs:schema>
        """;

    private static string Main(string importLocation, bool useImported = true) => $"""
        <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" xmlns:c="urn:common" xmlns="urn:order"
                   targetNamespace="urn:order" elementFormDefault="qualified">
          <xs:import namespace="urn:common" schemaLocation="{importLocation}"/>
          <xs:include schemaLocation="parts/id.xsd"/>
          <xs:element name="order">
            <xs:complexType><xs:sequence>
              <xs:element ref="id"/>
              <xs:element name="currency" type="{(useImported ? "c:Code" : "xs:string")}"/>
              <xs:any namespace="urn:common" processContents="lax" minOccurs="0"/>
            </xs:sequence></xs:complexType>
          </xs:element>
        </xs:schema>
        """;

    private const string Valid = """<order xmlns="urn:order"><id>1</id><currency>EUR</currency></order>""";

    // ── import and include in nested directories ──

    [Fact]
    public void ImportAndInclude_InNestedDirectories_Load()
    {
        Write("schemas/common/common.xsd", Common);
        Write("schemas/order/parts/id.xsd", Part);
        var main = Write("schemas/order/order.xsd", Main("../common/common.xsd"));

        var validator = XsdValidator.FromFile(main, schemaRoot: "..");

        validator.Validate(Body(Valid)).IsValid.Should().BeTrue();
        validator.Validate(Body("""<order xmlns="urn:order"><id>1</id><currency>euro</currency></order>"""))
            .IsValid.Should().BeFalse("the imported type's pattern applies");
    }

    [Fact]
    public void AnImportOutsideTheRoot_IsAnError_EvenWhenUnused()
    {
        Write("common/common.xsd", Common);
        Write("order/parts/id.xsd", Part);
        var main = Write("order/order.xsd", Main("../common/common.xsd", useImported: false));

        var act = () => XsdValidator.FromFile(main);

        act.Should().Throw<XmlSchemaException>()
            .WithMessage("validateXsd: the schema refers to '*common.xsd', outside '*order*'; imports and includes stay inside the package.");
    }

    [Fact]
    public void ANetworkImport_IsAnError_EvenWhenUnused()
    {
        Write("parts/id.xsd", Part);
        var main = Write("order.xsd", Main("http://127.0.0.1:9/common.xsd", useImported: false));

        var act = () => XsdValidator.FromFile(main);

        act.Should().Throw<XmlSchemaException>()
            .WithMessage("validateXsd: the schema refers to 'http://127.0.0.1:9/common.xsd'; a schema and its imports are read from the package only, not the network.");
    }

    [Fact]
    public void AMissingImport_IsAnError_NamingTheSchema()
    {
        Write("parts/id.xsd", Part);
        var main = Write("order.xsd", Main("common.xsd", useImported: false));

        var act = () => XsdValidator.FromFile(main);

        act.Should().Throw<XmlSchemaException>().WithMessage($"validateXsd: schema '{main}': *");
    }

    [Fact]
    public void ADoctypeInAnImportedFile_IsSkipped()
    {
        Write("common.xsd", """
            <?xml version="1.0"?>
            <!DOCTYPE schema PUBLIC "-//W3C//DTD XMLSchema 200102//EN" "http://www.w3.org/2001/XMLSchema.dtd" [
              <!ATTLIST schema xmlns:c CDATA #FIXED "urn:common">
            ]>
            """ + Common);
        Write("parts/id.xsd", Part);
        var main = Write("order.xsd", Main("common.xsd"));

        XsdValidator.FromFile(main).Validate(Body(Valid)).IsValid.Should().BeTrue();
    }

    // ── the root element across an imported namespace ──

    [Fact]
    public void TheRootElementCheck_KnowsTheGlobalElementsOfImportedSchemas()
    {
        Write("common.xsd", Common);
        Write("parts/id.xsd", Part);
        var validator = XsdValidator.FromFile(Write("order.xsd", Main("common.xsd")));

        validator.Validate(Body("""<note xmlns="urn:common">hello</note>""")).IsValid
            .Should().BeTrue("a global element of an imported schema is a root of the set");
        validator.Validate(Body("""<note xmlns="urn:other">hello</note>""")).Errors
            .Should().ContainSingle().Which.Should().Be("Root element '{urn:other}note' is not declared in the schema (cvc-elt.1.a)");
    }

    [Fact]
    public void ALaxWildcardOverAnImportedNamespace_IsValidatedOnceTheImportLoads()
    {
        Write("common.xsd", Common);
        Write("parts/id.xsd", Part);
        var validator = XsdValidator.FromFile(Write("order.xsd", Main("common.xsd")));

        validator.Validate(Body("""<order xmlns="urn:order" xmlns:c="urn:common"><id>1</id><currency>EUR</currency><c:amount>7</c:amount></order>"""))
            .IsValid.Should().BeTrue();
        validator.Validate(Body("""<order xmlns="urn:order" xmlns:c="urn:common"><id>1</id><currency>EUR</currency><c:amount>seven</c:amount></order>"""))
            .IsValid.Should().BeFalse("the element has a declaration now, so lax content is validated against it");
    }

    // ── standard multi-file schemas ──

    [Fact]
    public void Ubl21Invoice_ValidatesItsExample_WithTheRootAboveMaindoc()
    {
        var validator = XsdValidator.FromFile(Path.Combine(Fixtures, "ubl", "maindoc", "UBL-Invoice-2.1.xsd"), schemaRoot: "..");

        validator.Validate(Body(File.ReadAllText(Path.Combine(Fixtures, "ubl", "xml", "UBL-Invoice-2.1-Example.xml"))))
            .IsValid.Should().BeTrue();
    }

    [Fact]
    public void Ubl21Invoice_WithoutTheRoot_RefusesItsCommonImports()
    {
        var act = () => XsdValidator.FromFile(Path.Combine(Fixtures, "ubl", "maindoc", "UBL-Invoice-2.1.xsd"));

        act.Should().Throw<XmlSchemaException>().WithMessage("validateXsd: the schema refers to '*common*', outside '*maindoc*'*");
    }

    [Fact]
    public void WsSecurity_LoadsXmldsigWithItsDoctype()
    {
        var act = () => XsdValidator.FromFile(Path.Combine(Fixtures, "ebms", "oasis-200401-wss-wssecurity-secext-1.0.xsd"));

        act.Should().NotThrow();
    }

    // ── the route forms ──

    private sealed class PackageResolver(string root) : IRouteResourceResolver
    {
        public string? Resolve(string reference)
        {
            var candidate = Path.Combine(root, reference);
            return File.Exists(candidate) ? candidate : null;
        }

        public string DescribeSearch(string reference) => Path.Combine(root, reference);
    }

    [Fact]
    public async Task ValidateXsdFile_ResolvesThroughTheResourceResolver_AndReadsTheImports()
    {
        Write("resources/schemas/common.xsd", Common);
        Write("resources/schemas/parts/id.xsd", Part);
        Write("resources/schemas/order.xsd", Main("common.xsd"));
        _context.AddService(typeof(IRouteResourceResolver), new PackageResolver(_dir));
        _context.AddRoutes(r => r.From("direct://xsd-file-in").ValidateXsdFile("resources/schemas/order.xsd"));
        await _context.Start();
        var producer = _context.GetEndpoint("direct://xsd-file-in").CreateProducer();
        await producer.Start();

        var good = new Exchange(new Message(Valid));
        await producer.Process(good);
        var bad = () => producer.Process(new Exchange(new Message("""<order xmlns="urn:order"><id>1</id><currency>euro</currency></order>""")));

        good.Exception.Should().BeNull();
        await bad.Should().ThrowAsync<ValidationException>().WithMessage("*'urn:common:Code'*Pattern*");
    }

    [Fact]
    public async Task ValidatorEndpoint_ReadsTheImportsUnderSchemaRoot()
    {
        Write("schemas/common/common.xsd", Common);
        Write("schemas/order/parts/id.xsd", Part);
        var main = Write("schemas/order/order.xsd", Main("../common/common.xsd"));
        var uri = $"validator:{main.Replace('\\', '/')}?schemaRoot=..";
        _context.AddComponent(new ValidatorComponent());
        _context.AddRoutes(r => r.From("direct://xsd-endpoint-in").To(uri));
        await _context.Start();
        var producer = _context.GetEndpoint("direct://xsd-endpoint-in").CreateProducer();
        await producer.Start();

        var exchange = new Exchange(new Message(Valid));
        await producer.Process(exchange);

        exchange.Exception.Should().BeNull();
    }

    [Fact]
    public void ValidatorEndpoint_WithSchemaRootOnAJsonSchema_IsRefused()
    {
        var json = Write("schema.json", """{"type":"object"}""");
        var component = new ValidatorComponent();

        var act = () => component.CreateEndpoint(new EndpointUri("validator", json, "validator:" + json,
            new Dictionary<string, string> { ["schemaRoot"] = ".." }));

        act.Should().Throw<ArgumentException>().WithMessage("schemaRoot applies to XSD schemas*");
    }
}

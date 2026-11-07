using System.Xml.Linq;
using AwesomeAssertions;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Xml;

namespace redb.Route.Tests.Xml;

/// <summary>
/// Row 37 in markup: <c>&lt;validateXsd file="…" schemaRoot="…"/&gt;</c> keeps the file, so the schema's imports and
/// includes resolve relative to it; the code generator writes <c>ValidateXsdFile(…)</c> for it, and inline content
/// stays <c>ValidateXsd(…)</c> with the text.
/// </summary>
public sealed class ValidateXsdFileXmlTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "xsd-xml-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly RouteContext _context = new();

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "common"));
        Directory.CreateDirectory(Path.Combine(_dir, "order"));
        File.WriteAllText(Path.Combine(_dir, "common", "common.xsd"), """
            <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" targetNamespace="urn:common" elementFormDefault="qualified">
              <xs:simpleType name="Code"><xs:restriction base="xs:string"><xs:pattern value="[A-Z]{3}"/></xs:restriction></xs:simpleType>
            </xs:schema>
            """);
        File.WriteAllText(Path.Combine(_dir, "order", "order.xsd"), """
            <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" xmlns:c="urn:common" targetNamespace="urn:order" elementFormDefault="qualified">
              <xs:import namespace="urn:common" schemaLocation="../common/common.xsd"/>
              <xs:element name="order"><xs:complexType><xs:sequence><xs:element name="currency" type="c:Code"/></xs:sequence></xs:complexType></xs:element>
            </xs:schema>
            """);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _context.DisposeAsync();
        Directory.Delete(_dir, recursive: true);
    }

    private string Schema => Path.Combine(_dir, "order", "order.xsd").Replace('\\', '/');

    private static string Routes(string step) => $"""
        <routes xmlns="urn:redb:route:1.0">
          <route id="xsd-file">
            <from uri="direct://xsd-file-in"/>
            {step}
          </route>
        </routes>
        """;

    [Fact]
    public async Task FileWithSchemaRoot_ReadsTheImports()
    {
        _context.AddXmlRoutesFromContent(Routes($"""<validateXsd file="{Schema}" schemaRoot=".."/>"""), "<inline>");
        await _context.Start();
        var producer = _context.GetEndpoint("direct://xsd-file-in").CreateProducer();
        await producer.Start();

        var good = new Exchange(new Message("""<order xmlns="urn:order"><currency>EUR</currency></order>"""));
        await producer.Process(good);
        var bad = () => producer.Process(new Exchange(new Message("""<order xmlns="urn:order"><currency>euro</currency></order>""")));

        good.Exception.Should().BeNull();
        await bad.Should().ThrowAsync<redb.Route.Validation.ValidationException>()
            .WithMessage("*'urn:common:Code'*Pattern*", "the imported type's pattern applies");
    }

    [Fact]
    public async Task FileWithoutSchemaRoot_RefusesTheImportOutsideItsDirectory_WhenTheRouteStarts()
    {
        _context.AddXmlRoutesFromContent(Routes($"""<validateXsd file="{Schema}"/>"""), "<inline>");

        var act = () => _context.Start();

        (await act.Should().ThrowAsync<Exception>())
            .Which.ToString().Should().Contain("validateXsd: the schema refers to").And.Contain("imports and includes stay inside the package");
    }

    [Fact]
    public void InlineContentWithSchemaRoot_IsAnError()
    {
        var act = () => _context.AddXmlRoutesFromContent(
            Routes("""<validateXsd schemaRoot=".."><![CDATA[<xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema"/>]]></validateXsd>"""),
            "<inline>");

        act.Should().Throw<XmlRouteException>().WithMessage("*schemaRoot=…> needs the file attribute*");
    }

    [Fact]
    public void AMissingFile_IsStillAnErrorWhereTheRouteIsParsed()
    {
        var act = () => _context.AddXmlRoutesFromContent(Routes("""<validateXsd file="nowhere/none.xsd"/>"""), "<inline>");

        act.Should().Throw<XmlRouteException>();
    }

    [Fact]
    public void TheCodeGenerator_WritesTheFileForm()
    {
        var code = XmlCodeGenerator.Generate(
            XDocument.Parse(Routes($"""<validateXsd file="{Schema}" targetNamespace="urn:order" schemaRoot=".." throwOnFailure="false"/>""")),
            "XsdFile", "Tests.Generated", sourceName: "<inline>");

        code.Should().Contain($"""ValidateXsdFile("{Schema}", "urn:order", "..", false)""");
    }

    [Fact]
    public void TheCodeGenerator_WritesTheFileFormWithDefaultsLeftOut()
    {
        var code = XmlCodeGenerator.Generate(
            XDocument.Parse(Routes($"""<validateXsd file="{Schema}" schemaRoot=".."/>""")),
            "XsdFile", "Tests.Generated", sourceName: "<inline>");

        code.Should().Contain($"""ValidateXsdFile("{Schema}", null, "..")""");
    }

    [Fact]
    public void TheCodeGenerator_KeepsInlineContentAsText()
    {
        var code = XmlCodeGenerator.Generate(
            XDocument.Parse(Routes("""<validateXsd><![CDATA[<xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema"/>]]></validateXsd>""")),
            "XsdInline", "Tests.Generated", sourceName: "<inline>");

        code.Should().Contain("ValidateXsd(null, ");
        code.Should().NotContain("ValidateXsdFile");
    }
}

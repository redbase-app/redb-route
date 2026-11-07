using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;
using AwesomeAssertions;
using redb.Route.DataFormats.XmlJson;
using redb.Route.DataFormats.XmlJson.Internal;

namespace redb.Route.Tests.DataFormats;

/// <summary>
/// xmljson on real schemas (step S9): the schemas of this repository and public ones (OASIS ebMS 3.0 with W3C
/// xmldsig and OASIS WS-Security, SOAP 1.1 and 1.2). Each sample is valid against its schema, goes XML to JSON to XML,
/// and the result is valid again and equal to the sample; at a repeated place one element and several give the same
/// JSON shape. The public schemas are kept under Fixtures/xmljson with their import locations made local.
/// </summary>
public sealed class XmlJsonRealSchemaTests
{
    private static string Repo([CallerFilePath] string file = "") => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, "..", ".."));

    private static string Fixture(params string[] parts) => Path.Combine([AppContext.BaseDirectory, "Fixtures", "xmljson", .. parts]);

    private static XmlSchemaSet Load(string path, string? root = null) => redb.Route.Validation.SchemaResolver.Load("xmljson", path, root);

    private static void Valid(XmlSchemaSet set, XDocument document)
    {
        var errors = new List<string>();
        document.Validate(set, (_, e) => errors.Add(e.Message));
        errors.Should().BeEmpty();
    }

    /// <summary>The sample is valid, its JSON comes back as the same valid document; returns the JSON.</summary>
    private static JsonNode RoundTrip(XmlSchemaSet set, string xml, Action<XmlJsonOptions>? configure = null)
    {
        var sample = XDocument.Parse(xml);
        Valid(set, sample);
        var options = new XmlJsonOptions { SchemaSet = set };
        configure?.Invoke(options);
        var toJson = new XmlToJson(options);
        var json = toJson.Convert(Encoding.UTF8.GetBytes(xml));
        var back = new JsonToXml(options).Convert(Encoding.UTF8.GetBytes(json.ToJsonString()));
        Valid(set, back);
        XNode.DeepEquals(Normalize(back.Root!), Normalize(sample.Root!)).Should().BeTrue(
            $"the document comes back as it was:\n{back.Root}\nversus\n{sample.Root}");
        return json;
    }

    /// <summary>Namespace declarations may move to the root; prefixes and layout aside, the elements must be equal.</summary>
    private static XElement Normalize(XElement element)
        => new(element.Name,
            element.Attributes().Where(a => !a.IsNamespaceDeclaration).OrderBy(a => a.Name.ToString()),
            element.Nodes().Select(n => n switch
            {
                XElement child => (object)Normalize(child),
                XText text when string.IsNullOrWhiteSpace(text.Value) && element.HasElements => null!,
                XText text => text.Value,
                _ => null!,
            }).Where(n => n is not null));

    // ── This repository ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void WorkerTemplate_Order()
    {
        var set = Load(Path.Combine(Repo(), "..", "redb.Templates", "templates", "redb-worker", "RedbWorker.Module", "Xml", "Order.xsd"));

        RoundTrip(set, "<Order><OrderId>A-1</OrderId><Customer>Ann</Customer><Amount>12.50</Amount><Currency>EUR</Currency></Order>")
            ["Order"]!["Amount"]!.GetValue<decimal>().Should().Be(12.50m);
    }

    [Fact]
    public void RouteXmlTemplate_OrderWithLines()
    {
        var set = Load(Path.Combine(Repo(), "..", "redb.Templates", "templates", "route-xml", "redb-route-validator-xslt", "resources", "order.xsd"));

        var one = RoundTrip(set, """<order id="7"><customer>Ann</customer><line sku="a" qty="1" price="2.5"/></order>""");
        var two = RoundTrip(set, """<order id="7"><customer>Ann</customer><line sku="a" qty="1" price="2.5"/><line sku="b" qty="2" price="1"/></order>""");

        one["order"]!["line"].Should().BeOfType<JsonArray>();
        two["order"]!["line"]!.AsArray().Count.Should().Be(2);
        one["order"]!["line"]![0]!["@qty"]!.GetValue<decimal>().Should().Be(1);
    }

    [Fact]
    public void SerialNumbersDemo_Request()
    {
        var set = Load(Path.Combine(Repo(), "demos", "SerialNumbersDemo", "SerialNumbers.Xml", "resources", "SerialNumberRequest.xsd"));

        RoundTrip(set, "<SerialNumberRequest><RequestId>r1</RequestId><Gtin>04600000000001</Gtin><Quantity>3</Quantity></SerialNumberRequest>")
            ["SerialNumberRequest"]!["Gtin"]!.GetValue<string>().Should().Be("04600000000001", "a pattern-restricted string keeps its leading zero");
    }

    [Fact]
    public void RouteXmlSchema_ARouteFile()
    {
        var set = Load(Path.Combine(Repo(), "demos", "XmlDemo", "schema", "redb-route-1.0.xsd"));
        const string one = """
            <routes xmlns="urn:redb:route:1.0">
              <route id="a">
                <from uri="direct://a"/>
                <setHeader name="kind" value="order"/>
                <to uri="log:x"/>
              </route>
            </routes>
            """;
        const string two = """
            <routes xmlns="urn:redb:route:1.0">
              <route id="a">
                <from uri="direct://a"/>
                <to uri="log:x"/>
                <to uri="log:y"/>
              </route>
              <route id="b"><from uri="direct://b"/><to uri="log:z"/></route>
            </routes>
            """;

        var single = RoundTrip(set, one, o => o.Namespaces = XmlJsonNamespaces.Drop);
        var several = RoundTrip(set, two, o => o.Namespaces = XmlJsonNamespaces.Drop);

        single["routes"]!["route"].Should().BeOfType<JsonArray>("route repeats in the schema");
        single["routes"]!["route"]![0]!["to"].Should().BeOfType<JsonArray>("the steps are a repeated choice");
        several["routes"]!["route"]![0]!["to"]!.AsArray().Count.Should().Be(2);
    }

    // ── Public schemas ──────────────────────────────────────────────────────────────────────

    private const string Messaging = """
        <eb:Messaging xmlns:eb="http://docs.oasis-open.org/ebxml-msg/ebms/v3.0/ns/core/200704/">
          <eb:UserMessage>
            <eb:MessageInfo>
              <eb:Timestamp>2026-11-05T10:00:00Z</eb:Timestamp>
              <eb:MessageId>a@example.com</eb:MessageId>
            </eb:MessageInfo>
            <eb:PartyInfo>
              <eb:From><eb:PartyId type="urn:oasis:names:tc:ebcore:partyid-type:unregistered">sender</eb:PartyId><eb:Role>http://example.com/sender</eb:Role></eb:From>
              <eb:To><eb:PartyId type="urn:oasis:names:tc:ebcore:partyid-type:unregistered">receiver</eb:PartyId><eb:Role>http://example.com/receiver</eb:Role></eb:To>
            </eb:PartyInfo>
            <eb:CollaborationInfo>
              <eb:Service>http://example.com/service</eb:Service>
              <eb:Action>Submit</eb:Action>
              <eb:ConversationId>1</eb:ConversationId>
            </eb:CollaborationInfo>
            <eb:PayloadInfo>
              <eb:PartInfo href="cid:part1@example.com"/>
            </eb:PayloadInfo>
          </eb:UserMessage>
        </eb:Messaging>
        """;

    [Fact]
    public void Ebms3_Messaging()
    {
        var set = Load(Fixture("ebms", "ebms-header-3_0-200704.xsd"));

        var json = RoundTrip(set, Messaging);

        var user = json["eb:Messaging"]!["eb:UserMessage"]!;
        user.Should().BeOfType<JsonArray>("UserMessage repeats in the Messaging content");
        user[0]!["eb:PartyInfo"]!["eb:From"]!["eb:PartyId"].Should().BeOfType<JsonArray>("PartyId repeats");
        user[0]!["eb:PayloadInfo"]!["eb:PartInfo"].Should().BeOfType<JsonArray>("PartInfo repeats");
    }

    [Fact]
    public void WsSecurity_SecextWithXmldsigAndUtility_Loads()
    {
        // xmldsig-core-schema.xsd starts with a DOCTYPE; a schema's own DOCTYPE is skipped.
        var set = Load(Fixture("ebms", "oasis-200401-wss-wssecurity-secext-1.0.xsd"));

        const string security = """
            <wsse:Security xmlns:wsse="http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-secext-1.0.xsd"
                           xmlns:wsu="http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-utility-1.0.xsd">
              <wsu:Timestamp wsu:Id="ts"><wsu:Created>2026-11-05T10:00:00Z</wsu:Created><wsu:Expires>2026-11-05T10:05:00Z</wsu:Expires></wsu:Timestamp>
            </wsse:Security>
            """;
        RoundTrip(set, security)["wsse:Security"]!["wsu:Timestamp"].Should().NotBeNull();
    }

    [Theory]
    [InlineData("soap11-envelope.xsd", "http://schemas.xmlsoap.org/soap/envelope/")]
    [InlineData("soap12-envelope.xsd", "http://www.w3.org/2003/05/soap-envelope")]
    public void Soap_Envelopes(string file, string ns)
    {
        var set = Load(Fixture("ebms", file));

        var json = RoundTrip(set, $"""<s:Envelope xmlns:s="{ns}"><s:Header/><s:Body><Order xmlns="urn:o"><Id>1</Id></Order></s:Body></s:Envelope>""");

        json["s:Envelope"]!["s:Body"]!["Order"]![0]!["Id"]!.GetValue<string>().Should().Be("1", "Body is an unbounded xs:any: its content is an array and falls back below");
    }

    [Fact]
    public void Ubl21_TheOfficialInvoiceExample()
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var set = Load(Fixture("ubl", "maindoc", "UBL-Invoice-2.1.xsd"), "..");
        var compile = watch.Elapsed;

        var json = RoundTrip(set, File.ReadAllText(Fixture("ubl", "xml", "UBL-Invoice-2.1-Example.xml")),
            o => o.Namespaces = XmlJsonNamespaces.Keep);

        var invoice = json["Invoice"]!;
        invoice["cac:InvoiceLine"].Should().BeOfType<JsonArray>("InvoiceLine repeats");
        invoice["cbc:Note"].Should().BeOfType<JsonArray>("Note repeats even where the example has one");
        invoice["cac:AccountingSupplierParty"].Should().BeOfType<JsonObject>("the supplier is single");
        // Reported for the release notes, not asserted: the time to compile the UBL 2.1 Invoice schema set.
        Console.WriteLine($"UBL 2.1 Invoice schema compile: {compile.TotalMilliseconds:F0} ms");
    }

    // ── The schema reader and DTDs ──────────────────────────────────────────────────────────

    [Fact]
    public void ASchemaWhoseContentUsesAnEntity_FailsClearly()
    {
        var dir = Path.Combine(Path.GetTempPath(), "redb-xmljson-dtd-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "entity.xsd");
            File.WriteAllText(path, """
                <?xml version="1.0"?>
                <!DOCTYPE schema [<!ENTITY ns 'urn:x'>]>
                <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" targetNamespace="&ns;"/>
                """);

            var act = () => Load(path);

            act.Should().Throw<XmlException>().WithMessage("*ns*");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void AMessageWithADoctype_IsStillRefused()
    {
        var set = Load(Fixture("ebms", "soap11-envelope.xsd"));
        var converter = new XmlToJson(new XmlJsonOptions { SchemaSet = set });

        var act = () => converter.Convert(Encoding.UTF8.GetBytes("""<!DOCTYPE a [<!ENTITY e "x">]><a>&e;</a>"""));

        act.Should().Throw<XmlException>();
    }
}

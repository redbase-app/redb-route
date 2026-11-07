using System.Text;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using AwesomeAssertions;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.DataFormats.XmlJson;
using redb.Route.Xml;

namespace redb.Route.Tests.Xml;

/// <summary>
/// The xmljson format in markup and in the DSL (step S7): the default instance under the name <c>xmljson</c>, a named
/// instance with its options in <c>context.xml</c>, the per-step <c>type=</c> form, both directions, a schema path
/// resolved through the context's resource resolver (the package root), and the content types it writes.
/// </summary>
public sealed class XmlJsonMarkupTests : IAsyncLifetime
{
    private static readonly string FormatType =
        $"{typeof(XmlJsonDataFormat).FullName}, {typeof(XmlJsonDataFormat).Assembly.GetName().Name}";

    private readonly string _package = Path.Combine(Path.GetTempPath(), "redb-xmljson-pkg-" + Guid.NewGuid().ToString("N"));
    private readonly RouteContext _context = new();

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(Path.Combine(_package, "schemas"));
        File.WriteAllText(Path.Combine(_package, "schemas", "order.xsd"), """
            <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema">
              <xs:element name="Order"><xs:complexType><xs:sequence>
                <xs:element name="Id" type="xs:int"/>
                <xs:element name="Tag" type="xs:string" maxOccurs="unbounded"/>
              </xs:sequence></xs:complexType></xs:element>
            </xs:schema>
            """);
        // The package root only the context's resolver knows, as a Tsak package has it.
        _context.AddService(typeof(IRouteResourceResolver), new RouteResourceResolver { ResourceRoot = _package });
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _context.DisposeAsync();
        Directory.Delete(_package, recursive: true);
    }

    private void LoadContext(string inner)
        => new XmlContextLoader(_context).Load(XDocument.Parse($"""
            <context xmlns="urn:redb:route:1.0">
              {inner}
            </context>
            """, LoadOptions.SetLineInfo), "context.xml");

    private async Task<IMessage> Run(string step, object body)
    {
        IMessage? seen = null;
        _context.AddRoutes(r => r.From("direct://xj-out").Process(e => seen = e.In));
        _context.AddXmlRoutesFromContent($"""
            <routes xmlns="urn:redb:route:1.0">
              <route id="xj">
                <from uri="direct://xj-in"/>
                {step}
                <to uri="direct://xj-out"/>
              </route>
            </routes>
            """);
        await _context.Start();
        var producer = _context.GetEndpoint("direct://xj-in").CreateProducer();
        await producer.Start();
        await producer.Process(new Exchange(new Message(body)));
        return seen!;
    }

    private static string Text(object? body) => body is byte[] bytes ? Encoding.UTF8.GetString(bytes) : body?.ToString() ?? "";

    [Fact]
    public async Task TheDefaultInstance_AnswersToXmljson()
    {
        _context.AddXmlJsonDataFormat();

        var message = await Run("""<marshal format="xmljson"/>""", "<o><i>1</i><i>2</i></o>");

        Text(message.Body).Should().Be("""{"o":{"i":["1","2"]}}""");
        message.ContentType.Should().Be("application/json");
    }

    [Fact]
    public async Task ANamedInstanceInContextXml_ReadsItsSchemaFromThePackage()
    {
        LoadContext($"""<dataFormat type="{FormatType}" name="orders-json" schema="schemas/order.xsd"/>""");

        var message = await Run("""<marshal format="orders-json"/>""", "<Order><Id>7</Id><Tag>a</Tag></Order>");

        Text(message.Body).Should().Be("""{"Order":{"Id":7,"Tag":["a"]}}""", "the schema says Id is an int and Tag repeats");
    }

    [Fact]
    public async Task ThePerStepForm_TakesItsOptions()
    {
        var message = await Run($"""<marshal type="{FormatType}" arrays="//Item,//Tag" plural="true" namespaces="Drop"/>""",
            """<x:r xmlns:x="urn:x"><Items><Item>1</Item></Items><Tag>t</Tag></x:r>""");

        Text(message.Body).Should().Be("""{"r":{"Items":{"Item":["1"]},"Tag":["t"]}}""");
    }

    [Fact]
    public async Task Unmarshal_JsonToXml_WithRootAndPrefixes()
    {
        var message = await Run($"""<unmarshal type="{FormatType}" target="System.String" root="Order"/>""", """{"Id":"1","Tag":["a","b"]}""");

        Text(message.Body).Should().Be("""<?xml version="1.0" encoding="utf-8"?><Order><Id>1</Id><Tag>a</Tag><Tag>b</Tag></Order>""");
        message.ContentType.Should().Be("application/xml");
    }

    [Fact]
    public async Task Unmarshal_XmlToTheJsonTree()
    {
        var message = await Run($"""<unmarshal type="{FormatType}" target="System.Text.Json.Nodes.JsonNode, System.Text.Json"/>""", "<o><a>1</a></o>");

        message.Body.Should().BeAssignableTo<JsonNode>().Which["o"]!["a"]!.GetValue<string>().Should().Be("1");
    }

    [Fact]
    public async Task TheDsl_BothDirections()
    {
        string? json = null, xml = null;
        _context.AddRoutes(r => r.From("direct://dsl").MarshalXmlJson(o => o.Arrays.Add("//i"))
            .Process(e => json = Text(e.In.Body))
            .UnmarshalXmlJson<string>()
            .Process(e => xml = Text(e.In.Body)));
        await _context.Start();
        var producer = _context.GetEndpoint("direct://dsl").CreateProducer();
        await producer.Start();
        await producer.Process(new Exchange(new Message("<o><i>1</i></o>")));

        json.Should().Be("""{"o":{"i":["1"]}}""");
        xml.Should().Be("""<?xml version="1.0" encoding="utf-8"?><o><i>1</i></o>""");
    }

    [Fact]
    public void AMalformedPrefixesOption_IsAnErrorOfTheDocument()
    {
        var act = () => LoadContext($"""<dataFormat type="{FormatType}" name="bad" prefixes="o"/>""");

        act.Should().Throw<Exception>().Where(e => e.ToString().Contains("is not prefix=uri"));
    }

    [Fact]
    public void TheSerializerOptions_RoundTripTheirStringForms()
    {
        var format = new XmlJsonDataFormat { Arrays = " //Item , /Order/Tag ", Prefixes = "o=urn:orders, x = urn:x" };

        format.Options.Arrays.Should().Equal("//Item", "/Order/Tag");
        format.Options.Prefixes.Should().BeEquivalentTo(new Dictionary<string, string> { ["o"] = "urn:orders", ["x"] = "urn:x" });
        format.Arrays.Should().Be("//Item,/Order/Tag");
    }
}

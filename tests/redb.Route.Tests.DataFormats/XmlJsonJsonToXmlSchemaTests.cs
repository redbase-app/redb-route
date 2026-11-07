using System.Text;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;
using AwesomeAssertions;
using redb.Route.DataFormats.XmlJson;
using redb.Route.DataFormats.XmlJson.Internal;

namespace redb.Route.Tests.DataFormats;

/// <summary>
/// xmljson, JSON to XML by a schema (step S6): sequence order whatever the key order, choice branches, all, the
/// schema's namespaces and prefixes, lexical forms, strict errors with the JSON path, a wrapper only where the schema
/// has one. Every document written is validated against the schema.
/// </summary>
public sealed class XmlJsonJsonToXmlSchemaTests
{
    private const string Orders = """
        <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" targetNamespace="urn:orders" xmlns="urn:orders" elementFormDefault="qualified">
          <xs:element name="Order">
            <xs:complexType>
              <xs:sequence>
                <xs:element name="Id" type="xs:int"/>
                <xs:element name="Paid" type="xs:boolean"/>
                <xs:element name="Total" type="xs:decimal"/>
                <xs:choice>
                  <xs:element name="Card" type="xs:string"/>
                  <xs:element name="Cash" type="xs:string"/>
                </xs:choice>
                <xs:element name="Note" type="xs:string" nillable="true" minOccurs="0"/>
                <xs:element name="Code" type="xs:string" minOccurs="0"/>
                <xs:element name="Items">
                  <xs:complexType><xs:sequence><xs:element name="Item" type="Line" maxOccurs="unbounded"/></xs:sequence></xs:complexType>
                </xs:element>
              </xs:sequence>
              <xs:attribute name="channel" type="xs:string"/>
              <xs:attribute name="count" type="xs:int"/>
            </xs:complexType>
          </xs:element>
          <xs:complexType name="Line">
            <xs:all><xs:element name="Sku" type="xs:string"/><xs:element name="Qty" type="xs:int"/></xs:all>
          </xs:complexType>
        </xs:schema>
        """;

    private static XmlSchemaSet Set(string xsd)
    {
        var set = new XmlSchemaSet();
        using var reader = XmlReader.Create(new StringReader(xsd));
        set.Add(null, reader);
        set.Compile();
        return set;
    }

    private static XDocument Convert(string json, string xsd = Orders, Action<XmlJsonOptions>? configure = null)
    {
        var set = Set(xsd);
        var options = new XmlJsonOptions { SchemaSet = set };
        configure?.Invoke(options);
        return new JsonToXml(options).Convert(Encoding.UTF8.GetBytes(json));
    }

    private static string Valid(XDocument document, string xsd = Orders)
    {
        var errors = new List<string>();
        document.Validate(Set(xsd), (_, e) => errors.Add(e.Message));
        errors.Should().BeEmpty();
        return document.Root!.ToString(SaveOptions.DisableFormatting);
    }

    private const string Shuffled = """
        {"Order":{"Items":{"Item":[{"Qty":2,"Sku":"pen"}]},"Cash":"yes","Total":1e3,"@count":2,"Paid":true,"Id":7,"@channel":"web"}}
        """;

    [Fact]
    public void SequenceOrder_NamespacesAndLexicalForms_FromTheSchema()
    {
        Valid(Convert(Shuffled)).Should().Be(
            """<ns0:Order xmlns:ns0="urn:orders" count="2" channel="web"><ns0:Id>7</ns0:Id><ns0:Paid>true</ns0:Paid><ns0:Total>1000</ns0:Total><ns0:Cash>yes</ns0:Cash><ns0:Items><ns0:Item><ns0:Sku>pen</ns0:Sku><ns0:Qty>2</ns0:Qty></ns0:Item></ns0:Items></ns0:Order>""");
    }

    [Fact]
    public void Prefixes_FromTheOptions_OrFromTheJson()
    {
        Valid(Convert(Shuffled, configure: o => o.Prefixes["o"] = "urn:orders")).Should().StartWith("<o:Order ");
        Valid(Convert("""{"o:Order":{"@xmlns:o":"urn:orders","Id":1,"Paid":false,"Total":1,"Card":"x","Items":{"Item":[{"Sku":"a","Qty":1}]}}}"""))
            .Should().StartWith("<o:Order xmlns:o=\"urn:orders\">");
    }

    [Fact]
    public void Root_NamesTheGlobalElement()
    {
        Valid(Convert("""{"Id":1,"Paid":false,"Total":1,"Card":"x","Items":{"Item":[{"Sku":"a","Qty":1}]}}""", configure: o => o.Root = "Order"))
            .Should().StartWith("<ns0:Order");
    }

    [Fact]
    public void AWrapper_OnlyWhereTheSchemaHasOne()
    {
        // "Items": [...] names the schema's wrapper Items, whose content is one repeated Item.
        Valid(Convert("""{"Order":{"Id":1,"Paid":false,"Total":1,"Card":"x","Items":[{"Sku":"a","Qty":1},{"Sku":"b","Qty":2}]}}"""))
            .Should().Contain("<ns0:Items><ns0:Item><ns0:Sku>a</ns0:Sku><ns0:Qty>1</ns0:Qty></ns0:Item><ns0:Item>");
    }

    [Fact]
    public void Nillable_IsXsiNil()
    {
        Valid(Convert("""{"Order":{"Id":1,"Paid":false,"Total":1,"Card":"x","Note":null,"Items":{"Item":[{"Sku":"a","Qty":1}]}}}"""))
            .Should().Contain("<ns0:Note xsi:nil=\"true\" />").And.Contain("xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\"");
    }

    [Fact]
    public void AStringStaysAsWritten()
    {
        Valid(Convert("""{"Order":{"Id":1,"Paid":false,"Total":"12.50","Card":"x","Code":"007","Items":{"Item":[{"Sku":"a","Qty":1}]}}}"""))
            .Should().Contain("<ns0:Total>12.50</ns0:Total>").And.Contain("<ns0:Code>007</ns0:Code>");
    }

    [Theory]
    [InlineData("""{"Order":{"Id":1,"Card":"x","Cash":"y"}}""", "xmljson: /Order has both 'Card' and 'Cash', two branches of one choice in the schema.")]
    [InlineData("""{"Order":{"Id":1,"Extra":"x"}}""", "xmljson: /Order/Extra: the content of /Order in the schema has no place for 'Extra'.")]
    [InlineData("""{"Order":{"@size":"1"}}""", "xmljson: /Order/@size: the schema declares no attribute 'size' here.")]
    [InlineData("""{"Order":{"Id":[1,2]}}""", "xmljson: /Order/Id is an array of 2, and the schema allows one 'Id' here.")]
    [InlineData("""{"Order":{"Id":null}}""", "xmljson: /Order/Id is null, and the schema does not declare it nillable.")]
    [InlineData("""{"Order":{"Items":"x"}}""", "xmljson: /Order/Items holds a value, and the schema declares element content: an object is expected.")]
    [InlineData("""{"Other":{}}""", "xmljson: the root 'Other' is not a global element of the schema.")]
    public void WhatTheSchemaDoesNotAllow_IsAnError(string json, string message)
    {
        var act = () => Convert(json);

        act.Should().Throw<InvalidOperationException>().WithMessage(message);
    }

    [Fact]
    public void UnqualifiedLocalElements_HaveNoNamespace()
    {
        const string xsd = """
            <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" targetNamespace="urn:u">
              <xs:element name="r"><xs:complexType><xs:sequence><xs:element name="a" type="xs:int"/></xs:sequence></xs:complexType></xs:element>
            </xs:schema>
            """;
        Valid(Convert("""{"r":{"a":5}}""", xsd), xsd).Should().Be("""<ns0:r xmlns:ns0="urn:u"><a>5</a></ns0:r>""");
    }

    [Fact]
    public void UnderAWildcard_ThePlainReverseApplies()
    {
        const string xsd = """
            <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema">
              <xs:element name="r"><xs:complexType><xs:sequence>
                <xs:element name="id" type="xs:int"/>
                <xs:any processContents="skip" minOccurs="0" maxOccurs="unbounded"/>
              </xs:sequence></xs:complexType></xs:element>
            </xs:schema>
            """;
        Valid(Convert("""{"r":{"free":{"x":["1","2"]},"id":3}}""", xsd), xsd).Should().Be("<r><id>3</id><free><x>1</x><x>2</x></free></r>");
    }

    [Fact]
    public void XmlToJsonToXml_RoundTripsByTheSchema()
    {
        const string xml = """<o:Order xmlns:o="urn:orders" channel="web"><o:Id>7</o:Id><o:Paid>true</o:Paid><o:Total>12.50</o:Total><o:Card>v</o:Card><o:Items><o:Item><o:Sku>a</o:Sku><o:Qty>1</o:Qty></o:Item></o:Items></o:Order>""";
        var options = new XmlJsonOptions { SchemaSet = Set(Orders) };
        var toJson = new XmlToJson(options);
        var json = toJson.Write(toJson.Convert(Encoding.UTF8.GetBytes(xml)));

        Valid(new JsonToXml(options).Convert(json)).Should().Be(xml);
    }
}

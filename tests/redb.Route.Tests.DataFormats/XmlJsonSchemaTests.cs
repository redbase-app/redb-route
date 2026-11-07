using System.Text;
using System.Xml;
using System.Xml.Schema;
using AwesomeAssertions;
using redb.Route.DataFormats.XmlJson;
using redb.Route.DataFormats.XmlJson.Internal;

namespace redb.Route.Tests.DataFormats;

/// <summary>
/// xmljson with a schema (step S3): repetition from the compiled particles (element maxOccurs, a repeated sequence or
/// choice, a ref whose maxOccurs sits on the particle, groups, extension, substitution, recursion, xs:any), typed
/// values, element content as an object, imports and includes from the package only.
/// </summary>
public sealed class XmlJsonSchemaTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "redb-xmljson-xsd-" + Guid.NewGuid().ToString("N"));

    public XmlJsonSchemaTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private const string Order = """
        <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema">
          <xs:element name="Order">
            <xs:complexType>
              <xs:sequence>
                <xs:element name="Id" type="xs:int"/>
                <xs:element name="Paid" type="xs:boolean"/>
                <xs:element name="Total" type="xs:decimal"/>
                <xs:element name="Rate" type="xs:double" minOccurs="0"/>
                <xs:element name="Code" type="xs:string"/>
                <xs:element name="Note" type="xs:string" nillable="true" minOccurs="0"/>
                <xs:element name="Items">
                  <xs:complexType>
                    <xs:sequence>
                      <xs:element name="Item" maxOccurs="unbounded">
                        <xs:complexType>
                          <xs:simpleContent>
                            <xs:extension base="xs:string">
                              <xs:attribute name="qty" type="xs:int"/>
                            </xs:extension>
                          </xs:simpleContent>
                        </xs:complexType>
                      </xs:element>
                    </xs:sequence>
                  </xs:complexType>
                </xs:element>
                <xs:element name="Tags" minOccurs="0">
                  <xs:complexType>
                    <xs:sequence>
                      <xs:element name="Tag" type="xs:string" minOccurs="0" maxOccurs="unbounded"/>
                    </xs:sequence>
                  </xs:complexType>
                </xs:element>
              </xs:sequence>
            </xs:complexType>
          </xs:element>
        </xs:schema>
        """;

    private static XmlSchemaSet Set(string xsd)
    {
        var set = new XmlSchemaSet();
        using var reader = XmlReader.Create(new StringReader(xsd));
        set.Add(null, reader);
        return set;
    }

    private static string Json(string xml, XmlSchemaSet set, Action<XmlJsonOptions>? configure = null)
    {
        var options = new XmlJsonOptions { SchemaSet = set, Namespaces = XmlJsonNamespaces.Drop };
        configure?.Invoke(options);
        var converter = new XmlToJson(options);
        return Encoding.UTF8.GetString(converter.Write(converter.Convert(Encoding.UTF8.GetBytes(xml))));
    }

    [Fact]
    public void TypesAndRepetition_FromTheSchema()
    {
        Json("""
             <Order><Id>7</Id><Paid>true</Paid><Total>12.50</Total><Rate>0.5</Rate><Code>007</Code>
               <Items><Item qty="2">pen</Item></Items><Tags/></Order>
             """, Set(Order))
            .Should().Be("""{"Order":{"Id":7,"Paid":true,"Total":12.50,"Rate":0.5,"Code":"007","Items":{"Item":[{"@qty":2,"#text":"pen"}]},"Tags":{}}}""");
    }

    [Fact]
    public void OneAndMany_HaveTheSameShape()
    {
        const string one = "<Order><Id>1</Id><Paid>0</Paid><Total>1</Total><Code>a</Code><Items><Item>a</Item></Items></Order>";
        const string two = "<Order><Id>1</Id><Paid>0</Paid><Total>1</Total><Code>a</Code><Items><Item>a</Item><Item>b</Item></Items></Order>";

        Json(one, Set(Order)).Should().Contain("\"Item\":[\"a\"]");
        Json(two, Set(Order)).Should().Contain("\"Item\":[\"a\",\"b\"]");
    }

    [Fact]
    public void Nillable_IsNull()
    {
        Json("""<Order xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"><Id>1</Id><Paid>1</Paid><Total>1</Total><Code>a</Code><Note xsi:nil="true"/><Items><Item>a</Item></Items></Order>""",
                Set(Order))
            .Should().Contain("\"Note\":null");
    }

    [Theory]
    [InlineData("<Order><Id>x</Id><Paid>1</Paid><Total>1</Total><Code>a</Code><Items><Item>a</Item></Items></Order>", "xmljson: /Order/Id holds 'x', which is not an xs:int as the schema declares.")]
    [InlineData("<Order><Id>1</Id><Paid>yes</Paid><Total>1</Total><Code>a</Code><Items><Item>a</Item></Items></Order>", "xmljson: /Order/Paid holds 'yes', which is not an xs:boolean as the schema declares.")]
    [InlineData("<Order><Id>1</Id><Paid>1</Paid><Total>1</Total><Code>a</Code><Items><Item qty=\"many\">a</Item></Items></Order>", "xmljson: /Order/Items/Item/@qty holds 'many', which is not an xs:int as the schema declares.")]
    [InlineData("<Order><Id>1</Id><Extra/></Order>", "xmljson: /Order holds 'Extra', which its content in the schema has no place for.")]
    [InlineData("<Other/>", "xmljson: the root element 'Other' (no namespace) is not a global element of the schema.")]
    public void WhatTheSchemaDoesNotAllow_IsAnError(string xml, string message)
    {
        var act = () => Json(xml, Set(Order));

        act.Should().Throw<InvalidOperationException>().WithMessage(message);
    }

    [Fact]
    public void ARepeatedChoice_MakesEachBranchAnArray()
    {
        const string xsd = """
            <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema">
              <xs:element name="r"><xs:complexType>
                <xs:choice maxOccurs="unbounded"><xs:element name="a" type="xs:string"/><xs:element name="b" type="xs:string"/></xs:choice>
              </xs:complexType></xs:element>
            </xs:schema>
            """;
        Json("<r><a>1</a></r>", Set(xsd)).Should().Be("""{"r":{"a":["1"]}}""");
    }

    [Fact]
    public void AnElementTwiceInOneSequence_IsAnArray()
    {
        const string xsd = """
            <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema">
              <xs:element name="r"><xs:complexType><xs:sequence>
                <xs:element name="a" type="xs:string"/><xs:element name="b" type="xs:string"/><xs:element name="a" type="xs:string" minOccurs="0"/>
              </xs:sequence></xs:complexType></xs:element>
            </xs:schema>
            """;
        Json("<r><a>1</a><b>2</b></r>", Set(xsd)).Should().Be("""{"r":{"a":["1"],"b":"2"}}""");
    }

    [Fact]
    public void ARefWithMaxOccursOnTheParticle_Repeats()
    {
        const string xsd = """
            <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema">
              <xs:element name="Line" type="xs:int"/>
              <xs:element name="r"><xs:complexType><xs:sequence>
                <xs:element ref="Line" maxOccurs="unbounded"/>
              </xs:sequence></xs:complexType></xs:element>
            </xs:schema>
            """;
        Json("<r><Line>5</Line></r>", Set(xsd)).Should().Be("""{"r":{"Line":[5]}}""");
    }

    [Fact]
    public void GroupsAndExtension_AreFollowed()
    {
        const string xsd = """
            <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema">
              <xs:group name="lines"><xs:sequence><xs:element name="Line" type="xs:int" maxOccurs="unbounded"/></xs:sequence></xs:group>
              <xs:complexType name="Base"><xs:sequence><xs:element name="Id" type="xs:int"/></xs:sequence></xs:complexType>
              <xs:complexType name="Derived"><xs:complexContent><xs:extension base="Base">
                <xs:sequence><xs:group ref="lines"/></xs:sequence>
              </xs:extension></xs:complexContent></xs:complexType>
              <xs:element name="r" type="Derived"/>
            </xs:schema>
            """;
        Json("<r><Id>1</Id><Line>2</Line></r>", Set(xsd)).Should().Be("""{"r":{"Id":1,"Line":[2]}}""");
    }

    [Fact]
    public void ASubstitutionGroupMember_TakesTheHeadsPlace()
    {
        const string xsd = """
            <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema">
              <xs:element name="Party" type="xs:string" abstract="true"/>
              <xs:element name="Buyer" type="xs:string" substitutionGroup="Party"/>
              <xs:element name="r"><xs:complexType><xs:sequence>
                <xs:element ref="Party" maxOccurs="unbounded"/>
              </xs:sequence></xs:complexType></xs:element>
            </xs:schema>
            """;
        Json("<r><Buyer>ann</Buyer></r>", Set(xsd)).Should().Be("""{"r":{"Buyer":["ann"]}}""");
    }

    [Fact]
    public void ARecursiveType_IsFollowedAsDeepAsTheDocument()
    {
        const string xsd = """
            <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema">
              <xs:complexType name="Node"><xs:sequence>
                <xs:element name="v" type="xs:int"/>
                <xs:element name="node" type="Node" minOccurs="0" maxOccurs="unbounded"/>
              </xs:sequence></xs:complexType>
              <xs:element name="node" type="Node"/>
            </xs:schema>
            """;
        Json("<node><v>1</v><node><v>2</v><node><v>3</v></node></node></node>", Set(xsd))
            .Should().Be("""{"node":{"v":1,"node":[{"v":2,"node":[{"v":3}]}]}}""");
    }

    [Fact]
    public void UnderAWildcard_AGlobalDeclarationIsUsed_ElseTheFallback()
    {
        const string xsd = """
            <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema">
              <xs:element name="Known" type="xs:int"/>
              <xs:element name="r"><xs:complexType><xs:sequence>
                <xs:any processContents="lax" minOccurs="0" maxOccurs="unbounded"/>
              </xs:sequence></xs:complexType></xs:element>
            </xs:schema>
            """;
        Json("<r><Known>4</Known><Free><x>1</x><x>2</x><y>3</y></Free></r>", Set(xsd))
            .Should().Be("""{"r":{"Known":[4],"Free":[{"x":["1","2"],"y":"3"}]}}""");
    }

    [Fact]
    public void ArraysPaths_StillAddArraysTheSchemaDoesNotKnow()
    {
        const string xsd = """
            <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema">
              <xs:element name="r"><xs:complexType><xs:sequence><xs:element name="a" type="xs:int"/></xs:sequence></xs:complexType></xs:element>
            </xs:schema>
            """;
        Json("<r><a>1</a></r>", Set(xsd), o => o.Arrays.Add("/r/a")).Should().Be("""{"r":{"a":[1]}}""");
    }

    [Fact]
    public void Namespaces_TheSchemaMatchesQualifiedNames()
    {
        const string xsd = """
            <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" targetNamespace="urn:o" xmlns="urn:o" elementFormDefault="qualified">
              <xs:element name="Order"><xs:complexType><xs:sequence><xs:element name="Id" type="xs:int" maxOccurs="2"/></xs:sequence></xs:complexType></xs:element>
            </xs:schema>
            """;
        Json("""<o:Order xmlns:o="urn:o"><o:Id>1</o:Id></o:Order>""", Set(xsd), o => o.Namespaces = XmlJsonNamespaces.Keep)
            .Should().Be("""{"o:Order":{"@xmlns:o":"urn:o","o:Id":[1]}}""");
    }

    [Fact]
    public void ImportsAndIncludes_ResolveInsideTheRoot()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "main"));
        Directory.CreateDirectory(Path.Combine(_dir, "common"));
        File.WriteAllText(Path.Combine(_dir, "common", "types.xsd"), """
            <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" targetNamespace="urn:t" xmlns="urn:t">
              <xs:simpleType name="Qty"><xs:restriction base="xs:int"/></xs:simpleType>
            </xs:schema>
            """);
        File.WriteAllText(Path.Combine(_dir, "main", "lines.xsd"), """
            <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" xmlns:t="urn:t">
              <xs:import namespace="urn:t" schemaLocation="../common/types.xsd"/>
              <xs:complexType name="Lines"><xs:sequence><xs:element name="Qty" type="t:Qty" maxOccurs="unbounded"/></xs:sequence></xs:complexType>
            </xs:schema>
            """);
        File.WriteAllText(Path.Combine(_dir, "main", "order.xsd"), """
            <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" xmlns:t="urn:t">
              <xs:import namespace="urn:t" schemaLocation="../common/types.xsd"/>
              <xs:include schemaLocation="lines.xsd"/>
              <xs:element name="Order" type="Lines"/>
            </xs:schema>
            """);
        var options = new XmlJsonOptions { Schema = Path.Combine(_dir, "main", "order.xsd"), SchemaRoot = _dir, Namespaces = XmlJsonNamespaces.Drop };
        var converter = new XmlToJson(options);

        Encoding.UTF8.GetString(converter.Write(converter.Convert(Encoding.UTF8.GetBytes("<Order><Qty>3</Qty></Order>"))))
            .Should().Be("""{"Order":{"Qty":[3]}}""");
    }

    [Fact]
    public void AnImportOutsideTheRoot_IsRefused()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "main"));
        File.WriteAllText(Path.Combine(_dir, "types.xsd"), """<xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" targetNamespace="urn:t"/>""");
        File.WriteAllText(Path.Combine(_dir, "main", "order.xsd"), """
            <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema">
              <xs:import namespace="urn:t" schemaLocation="../types.xsd"/>
              <xs:element name="Order" type="xs:string"/>
            </xs:schema>
            """);

        var act = () => new XmlToJson(new XmlJsonOptions { Schema = Path.Combine(_dir, "main", "order.xsd") });

        act.Should().Throw<XmlSchemaException>().WithMessage("*outside*imports and includes stay inside the package*");
    }

    [Fact]
    public void AnImportFromTheNetwork_IsRefused()
    {
        File.WriteAllText(Path.Combine(_dir, "order.xsd"), """
            <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema">
              <xs:import namespace="urn:t" schemaLocation="http://example.invalid/types.xsd"/>
              <xs:element name="Order" type="xs:string"/>
            </xs:schema>
            """);

        var act = () => new XmlToJson(new XmlJsonOptions { Schema = Path.Combine(_dir, "order.xsd") });

        act.Should().Throw<XmlSchemaException>().WithMessage("*from the package only, not the network*");
    }
}

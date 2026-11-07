using System.Text;
using AwesomeAssertions;
using redb.Route.DataFormats.XmlJson;
using redb.Route.DataFormats.XmlJson.Internal;

namespace redb.Route.Tests.DataFormats;

/// <summary>
/// xmljson, XML to JSON without a schema (step S2): the Json.NET notation, namespaces kept or dropped, the arrays
/// paths, and the fallback (a repeat is an array, a single element an object).
/// </summary>
public sealed class XmlJsonXmlToJsonTests
{
    private static string Json(string xml, Action<XmlJsonOptions>? configure = null)
    {
        var options = new XmlJsonOptions();
        configure?.Invoke(options);
        var converter = new XmlToJson(options);
        return Encoding.UTF8.GetString(converter.Write(converter.Convert(Encoding.UTF8.GetBytes(xml))));
    }

    [Theory]
    [InlineData("<a>5</a>", """{"a":"5"}""")]
    [InlineData("<a/>", """{"a":""}""")]
    [InlineData("<a></a>", """{"a":""}""")]
    [InlineData("<a>  x  </a>", """{"a":"  x  "}""")]
    [InlineData("<a>x<!-- c --><?pi y?>z</a>", """{"a":"xz"}""")]
    [InlineData("<a>Привет</a>", """{"a":"Привет"}""")]
    public void ATextOnlyElement_IsItsText(string xml, string json)
    {
        Json(xml).Should().Be(json);
    }

    [Fact]
    public void Cdata_IsText()
    {
        // The writer escapes HTML-sensitive characters; the parsed value is the CDATA content.
        System.Text.Json.Nodes.JsonNode.Parse(Json("<a><![CDATA[<b> & c]]></a>"))!["a"]!.GetValue<string>()
            .Should().Be("<b> & c");
    }

    [Theory]
    [InlineData("""<a xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xsi:nil="true"/>""", """{"a":null}""")]
    [InlineData("""<r xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"><a xsi:nil="1"/></r>""", """{"r":{"a":null}}""")]
    public void XsiNil_IsNull(string xml, string json)
    {
        Json(xml, o => o.Namespaces = XmlJsonNamespaces.Drop).Should().Be(json);
    }

    [Fact]
    public void XsiNil_BesideOtherAttributes_StaysAnAttribute()
    {
        Json("""<a xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" id="1" xsi:nil="true"/>""", o => o.Namespaces = XmlJsonNamespaces.Drop)
            .Should().Be("""{"a":{"@id":"1","@nil":"true"}}""");
    }

    [Theory]
    [InlineData("""<a id="1">x</a>""", """{"a":{"@id":"1","#text":"x"}}""")]
    [InlineData("""<a id="1"/>""", """{"a":{"@id":"1"}}""")]
    [InlineData("""<a id="1"><b>2</b></a>""", """{"a":{"@id":"1","b":"2"}}""")]
    [InlineData("<a>x<b>2</b>y</a>", """{"a":{"b":"2","#text":"xy"}}""")]
    [InlineData("<a>\n  <b>2</b>\n</a>", """{"a":{"b":"2"}}""")]
    public void AttributesAndText(string xml, string json)
    {
        Json(xml).Should().Be(json);
    }

    [Fact]
    public void TheKeys_AreConfigurable()
    {
        Json("""<a id="1">x</a>""", o => { o.TextKey = "$"; o.AttributePrefix = "-"; })
            .Should().Be("""{"a":{"-id":"1","$":"x"}}""");
    }

    [Fact]
    public void Namespaces_Keep_WritesPrefixesAndDeclarations()
    {
        Json("""<s:Envelope xmlns:s="urn:s" xmlns="urn:d"><s:Body><Order s:id="7">x</Order></s:Body></s:Envelope>""")
            .Should().Be("""{"s:Envelope":{"@xmlns:s":"urn:s","@xmlns":"urn:d","s:Body":{"Order":{"@s:id":"7","#text":"x"}}}}""");
    }

    [Fact]
    public void Namespaces_Drop_WritesLocalNames()
    {
        Json("""<s:Envelope xmlns:s="urn:s" xmlns="urn:d"><s:Body><Order s:id="7">x</Order></s:Body></s:Envelope>""",
                o => o.Namespaces = XmlJsonNamespaces.Drop)
            .Should().Be("""{"Envelope":{"Body":{"Order":{"@id":"7","#text":"x"}}}}""");
    }

    [Fact]
    public void Fallback_ARepeatIsAnArray_ASingleElementAnObject()
    {
        Json("<o><i>1</i><i>2</i></o>").Should().Be("""{"o":{"i":["1","2"]}}""");
        Json("<o><i>1</i></o>").Should().Be("""{"o":{"i":"1"}}""");
    }

    [Fact]
    public void Fallback_RepeatsGroupInFirstSeenOrder()
    {
        Json("<o><i>1</i><n>x</n><i>2</i></o>").Should().Be("""{"o":{"i":["1","2"],"n":"x"}}""");
    }

    [Theory]
    [InlineData("/Order/Items/Item")]
    [InlineData("//Item")]
    [InlineData("//Items/Item")]
    public void ArraysPaths_MakeASingleElementAnArray(string path)
    {
        Json("<Order><Items><Item>1</Item></Items></Order>", o => o.Arrays.Add(path))
            .Should().Be("""{"Order":{"Items":{"Item":["1"]}}}""");
    }

    [Theory]
    [InlineData("<Order><Items><Item>1</Item></Items></Order>")]
    [InlineData("<Order><Items><Item>1</Item><Item>2</Item></Items></Order>")]
    public void WithArraysPaths_OneAndManyHaveTheSameShape(string xml)
    {
        Json(xml, o => o.Arrays.Add("//Item")).Should().StartWith("""{"Order":{"Items":{"Item":[""");
    }

    [Fact]
    public void ArraysPaths_FromTheRoot_MatchOnlyThatPlace()
    {
        Json("<r><a><i>1</i></a><b><i>2</i></b></r>", o => o.Arrays.Add("/r/a/i"))
            .Should().Be("""{"r":{"a":{"i":["1"]},"b":{"i":"2"}}}""");
    }

    [Fact]
    public void ArraysPaths_APrefixedStep_ComparesTheWrittenName()
    {
        const string xml = """<r xmlns:x="urn:x" xmlns:y="urn:y"><x:i>1</x:i><y:j>2</y:j></r>""";

        Json(xml, o => o.Arrays.Add("//x:i")).Should().Contain("\"x:i\":[\"1\"]");
        Json(xml, o => o.Arrays.Add("//i")).Should().Contain("\"x:i\":[\"1\"]", "a step without a prefix is the local name");
        Json(xml, o => o.Arrays.Add("//y:i")).Should().Contain("\"x:i\":\"1\"");
    }

    [Theory]
    [InlineData("Order/Item")]
    [InlineData("/Order//Item")]
    [InlineData("/")]
    [InlineData("// Item")]
    public void AMalformedArraysPath_IsAnError(string path)
    {
        var act = () => new XmlToJson(new XmlJsonOptions { Arrays = { path } });

        act.Should().Throw<ArgumentException>().WithMessage($"xmljson arrays path '{path}'*");
    }

    [Fact]
    public void Indent_WritesIndentedJson()
    {
        Json("<a><b>1</b></a>", o => o.Indent = true).Should().Contain("\n");
    }

    [Fact]
    public void ADtd_IsRefused()
    {
        var act = () => Json("""<!DOCTYPE a [<!ENTITY e "x">]><a>&e;</a>""");

        act.Should().Throw<System.Xml.XmlException>();
    }
}

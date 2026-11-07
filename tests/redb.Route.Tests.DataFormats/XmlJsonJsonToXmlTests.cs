using System.Text;
using System.Xml.Linq;
using AwesomeAssertions;
using redb.Route.DataFormats.XmlJson;
using redb.Route.DataFormats.XmlJson.Internal;

namespace redb.Route.Tests.DataFormats;

/// <summary>
/// xmljson, JSON to XML without a schema (step S5): the reverse notation, root=, arrays into repeated elements, the
/// plural wrapper, namespaces from @xmlns keys, and the round trip of the S2 documents.
/// </summary>
public sealed class XmlJsonJsonToXmlTests
{
    private static string Xml(string json, Action<XmlJsonOptions>? configure = null)
    {
        var options = new XmlJsonOptions();
        configure?.Invoke(options);
        var converter = new JsonToXml(options);
        return converter.Convert(Encoding.UTF8.GetBytes(json)).Root!.ToString(SaveOptions.DisableFormatting);
    }

    [Theory]
    [InlineData("""{"a":"5"}""", "<a>5</a>")]
    [InlineData("""{"a":""}""", "<a></a>")]
    [InlineData("""{"a":12.50}""", "<a>12.50</a>")]
    [InlineData("""{"a":true}""", "<a>true</a>")]
    [InlineData("""{"a":{"@id":"1","#text":"x"}}""", """<a id="1">x</a>""")]
    [InlineData("""{"a":{"@id":7,"b":"2"}}""", """<a id="7"><b>2</b></a>""")]
    [InlineData("""{"a":{"@id":null,"b":"2"}}""", "<a><b>2</b></a>")]
    [InlineData("""{"o":{"i":["1","2"]}}""", "<o><i>1</i><i>2</i></o>")]
    [InlineData("""{"o":{"i":[]}}""", "<o />")]
    [InlineData("""{"a":"Привет"}""", "<a>Привет</a>")]
    public void TheNotation(string json, string xml)
    {
        Xml(json).Should().Be(xml);
    }

    [Fact]
    public void Null_IsXsiNil_WithTheNamespaceDeclared()
    {
        Xml("""{"r":{"a":null}}""").Should().Be("""<r><a xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xsi:nil="true" /></r>""");
    }

    [Fact]
    public void Namespaces_FromXmlnsKeys()
    {
        Xml("""{"s:Envelope":{"@xmlns:s":"urn:s","@xmlns":"urn:d","s:Body":{"Order":{"@s:id":"7","#text":"x"}}}}""")
            .Should().Be("""<s:Envelope xmlns:s="urn:s" xmlns="urn:d"><s:Body><Order s:id="7">x</Order></s:Body></s:Envelope>""");
    }

    [Fact]
    public void AnUndeclaredPrefix_IsAnError()
    {
        var act = () => Xml("""{"o:Order":{"Id":"1"}}""");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("xmljson: /o:Order: the prefix 'o' is not declared; declare it with an \"@xmlns:o\" key on this element or an enclosing one.");
    }

    [Fact]
    public void Root_WrapsTheWholeJson()
    {
        Xml("""{"Id":"1","Tag":["a","b"]}""", o => o.Root = "Order").Should().Be("<Order><Id>1</Id><Tag>a</Tag><Tag>b</Tag></Order>");
        Xml("""{"Item":["a","b"]}""", o => o.Root = "List").Should().Be("<List><Item>a</Item><Item>b</Item></List>");
        var act = () => Xml("""["a","b"]""", o => o.Root = "List");
        act.Should().Throw<InvalidOperationException>().WithMessage("xmljson: the JSON is an array, and its items have no element name*root=List.");
    }

    [Theory]
    [InlineData("""{"a":"1","b":"2"}""", "xmljson: the JSON is an object of 2 key(s); XML has one root element: give the JSON one top-level key, or name the root with root=.")]
    [InlineData("""["a"]""", "xmljson: the JSON is an array; XML has one root element: give the JSON one top-level key, or name the root with root=.")]
    [InlineData("""{"a":["1"]}""", "xmljson: the top-level key 'a' holds an array; XML has one root element: name the root with root=.")]
    [InlineData("""{"a":{"first name":"x"}}""", "xmljson: /a/first name: 'first name' is not an XML name.")]
    [InlineData("""{"a":{"1x":"x"}}""", "xmljson: /a/1x: '1x' is not an XML name.")]
    [InlineData("""{"a":{"b":[["1"]]}}""", "xmljson: /a/b is an array inside an array; XML repeats an element, it cannot nest a list in a list.")]
    [InlineData("""{"a":{"@id":{"x":"1"}}}""", "xmljson: /a/@id holds an object of 1 key(s), where XML has text: an attribute, a text key or a namespace.")]
    [InlineData("not json", "xmljson: the body is not JSON*")]
    public void WhatXmlCannotHold_IsAnError(string json, string message)
    {
        var act = () => Xml(json);

        act.Should().Throw<InvalidOperationException>().WithMessage(message);
    }

    [Fact]
    public void ThePluralRule_WrapsAnArrayInItsPlural()
    {
        Xml("""{"r":{"Incidents":["1","2"]}}""", o => o.Plural = true)
            .Should().Be("<r><Incidents><Incident>1</Incident><Incident>2</Incident></Incidents></r>");
        Xml("""{"r":{"Categories":["a"],"Addresses":["b"],"Cases":["c"]}}""", o => o.Plural = true)
            .Should().Be("<r><Categories><Category>a</Category></Categories><Addresses><Address>b</Address></Addresses><Cases><Case>c</Case></Cases></r>");
    }

    [Fact]
    public void ThePluralRule_IsSilentOnOtherNames_AndOffByDefault()
    {
        Xml("""{"r":{"Data":["1"]}}""", o => o.Plural = true).Should().Be("<r><Data>1</Data></r>");
        Xml("""{"r":{"Incidents":["1"]}}""").Should().Be("<r><Incidents>1</Incidents></r>");
    }

    [Fact]
    public void TheKeys_AreConfigurable()
    {
        Xml("""{"a":{"-id":"1","$":"x"}}""", o => { o.TextKey = "$"; o.AttributePrefix = "-"; }).Should().Be("""<a id="1">x</a>""");
    }

    [Theory]
    [InlineData("<a>5</a>")]
    [InlineData("<a/>")]
    [InlineData("""<a id="1">x</a>""")]
    [InlineData("""<a id="1"><b>2</b><b>3</b><c/></a>""")]
    [InlineData("""<s:Envelope xmlns:s="urn:s" xmlns="urn:d"><s:Body><Order s:id="7">x</Order></s:Body></s:Envelope>""")]
    [InlineData("""<r xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"><a xsi:nil="true"/><b>x</b></r>""")]
    [InlineData("<Order><Items><Item>1</Item></Items></Order>")]
    [InlineData("<a>Привет</a>")]
    public void XmlToJsonToXml_RoundTrips(string xml)
    {
        var options = new XmlJsonOptions { Arrays = { "//Item" } };
        var toJson = new XmlToJson(options);
        var json = toJson.Write(toJson.Convert(Encoding.UTF8.GetBytes(xml)));
        var back = new JsonToXml(options).Convert(json);

        Normalize(back.Root!).Should().Be(Normalize(XElement.Parse(xml)));
    }

    [Fact]
    public void Write_IsUtf8WithADeclarationAndNoBom()
    {
        var converter = new JsonToXml(new XmlJsonOptions());
        var bytes = converter.Write(converter.Convert(Encoding.UTF8.GetBytes("""{"a":"Ж"}""")));

        bytes[0].Should().Be((byte)'<');
        Encoding.UTF8.GetString(bytes).Should().Be("""<?xml version="1.0" encoding="utf-8"?><a>Ж</a>""");
    }

    /// <summary>An empty element written as <c>&lt;a&gt;&lt;/a&gt;</c> or <c>&lt;a/&gt;</c> is the same element.</summary>
    private static string Normalize(XElement element)
    {
        var copy = new XElement(element);
        foreach (var e in copy.DescendantsAndSelf().Where(e => !e.HasElements && e.Value.Length == 0))
            e.RemoveNodes();
        return copy.ToString(SaveOptions.DisableFormatting);
    }
}

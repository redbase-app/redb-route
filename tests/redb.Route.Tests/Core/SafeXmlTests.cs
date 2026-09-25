using System.Text;
using System.Xml;
using System.Xml.Linq;
using FluentAssertions;
using redb.Route.Core;

namespace redb.Route.Tests.Core;

/// <summary>
/// XML that came from somewhere else is read through <see cref="SafeXml"/>, which refuses a document
/// type declaration. The direct loaders expand one: measured on .NET 10, the 452-byte document below
/// becomes 300 000 characters through <c>XmlDocument.Load</c>, <c>XDocument.Load</c> and
/// <c>XDocument.Parse</c> alike, and <c>XmlResolver = null</c> changes nothing, because the expansion
/// runs on internal entities. Scaled up, one small message costs the worker gigabytes and takes every
/// route in the process down with it.
/// </summary>
public class SafeXmlTests
{
    /// <summary>Nested internal entities — the "billion laughs" shape, kept small enough to be a test.</summary>
    private static string BillionLaughs(int levels = 5, int fanOut = 10)
    {
        var sb = new StringBuilder("<?xml version=\"1.0\"?><!DOCTYPE lolz [<!ENTITY lol \"lol\">");
        for (var i = 1; i <= levels; i++)
        {
            sb.Append($"<!ENTITY lol{i} \"");
            for (var j = 0; j < fanOut; j++) sb.Append(i == 1 ? "&lol;" : $"&lol{i - 1};");
            sb.Append("\">");
        }
        return sb.Append($"]><lolz>&lol{levels};</lolz>").ToString();
    }

    private static byte[] Bytes(string xml) => Encoding.UTF8.GetBytes(xml);

    [Fact]
    public void The_direct_loaders_do_expand_entities_which_is_why_this_type_exists()
    {
        var xml = BillionLaughs();

        // Not a test of our code: a guard on the premise. If a future runtime starts refusing a DTD
        // here, this fails and the next reader of SafeXml learns the reason it was written is gone.
        XDocument.Parse(xml).Root!.Value.Length.Should().BeGreaterThan(100_000);
        var dom = new XmlDocument { XmlResolver = null };
        dom.Load(new MemoryStream(Bytes(xml)));
        dom.DocumentElement!.InnerText.Length.Should().BeGreaterThan(100_000);
    }

    [Fact]
    public void Every_loader_refuses_a_document_type_declaration()
    {
        var xml = BillionLaughs();
        var bytes = Bytes(xml);

        var loaders = new List<(string Name, Action Act)>
        {
            ("Load(byte[])", () => SafeXml.Load(bytes)),
            ("Load(Stream)", () => SafeXml.Load(new MemoryStream(bytes))),
            ("Parse(string)", () => SafeXml.Parse(xml)),
            ("ParseElement(string)", () => SafeXml.ParseElement(xml)),
            ("LoadDocument(byte[])", () => SafeXml.LoadDocument(bytes)),
        };

        foreach (var (name, act) in loaders)
            act.Should().Throw<XmlException>($"{name} must refuse a DTD instead of expanding it");
    }

    [Fact]
    public void A_dtd_without_entities_is_refused_just_the_same()
    {
        // The refusal is of the declaration, not of what it happens to contain: a DTD that looks
        // harmless today is still an entity expansion away from not being one.
        const string xml = "<?xml version=\"1.0\"?><!DOCTYPE note SYSTEM \"note.dtd\"><note/>";

        var act = () => SafeXml.Parse(xml);

        act.Should().Throw<XmlException>();
    }

    [Fact]
    public void Ordinary_documents_read_exactly_as_before()
    {
        const string xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <order xmlns="urn:acme" id="7">
              <item sku="A-1">Ящик</item>
              <note><![CDATA[a < b & c]]></note>
            </order>
            """;

        var doc = SafeXml.Parse(xml);
        var ns = XNamespace.Get("urn:acme");

        doc.Root!.Name.Should().Be(ns + "order");
        doc.Root.Attribute("id")!.Value.Should().Be("7");
        doc.Root.Element(ns + "item")!.Value.Should().Be("Ящик");
        doc.Root.Element(ns + "note")!.Value.Should().Be("a < b & c");

        SafeXml.Load(Bytes(xml)).Root!.Name.Should().Be(ns + "order");
        SafeXml.LoadDocument(Bytes(xml)).DocumentElement!.LocalName.Should().Be("order");
        SafeXml.ParseElement("<a><b/></a>").Name.LocalName.Should().Be("a");
    }

    [Fact]
    public void Line_info_and_whitespace_survive_the_reader()
    {
        // Two properties other call sites depend on: markup errors are reported by position, and a
        // signed document must keep its whitespace or canonicalization fails.
        var doc = SafeXml.Parse("<a>\n  <b/>\n</a>", LoadOptions.SetLineInfo | LoadOptions.PreserveWhitespace);

        ((IXmlLineInfo)doc.Root!.Element("b")!).LineNumber.Should().Be(2);
        doc.Root.ToString(SaveOptions.DisableFormatting).Should().Contain("\n  <b />");
        SafeXml.LoadDocument(Bytes("<a> <b/> </a>")).PreserveWhitespace.Should().BeTrue();
    }

    [Fact]
    public void A_document_over_the_size_bound_is_refused()
    {
        var big = "<a>" + new string('x', 5_000) + "</a>";

        var act = () => SafeXml.Parse(big, maxCharacters: 1_000);

        act.Should().Throw<XmlException>("a caller that knows a sane size can cap it");
    }
}

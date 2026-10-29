using System.Text;
using System.Xml;
using System.Xml.Linq;
using AwesomeAssertions;
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

    // ── Structure limits ─────────────────────────────────────────────────────

    private static string Nested(int levels) =>
        string.Concat(Enumerable.Repeat("<x>", levels)) + string.Concat(Enumerable.Repeat("</x>", levels));

    private static string Attributes(int count, string prefix = "a") =>
        "<x " + string.Join(' ', Enumerable.Range(0, count).Select(i => prefix == "xmlns" ? $"xmlns:p{i}=\"urn:{i}\"" : $"a{i}=\"v\"")) + "/>";

    public static TheoryData<string, string, XmlLimits> OverALimit() => new()
    {
        { nameof(XmlLimits.MaxDepth), Nested(XmlLimits.DefaultMaxDepth + 1), XmlLimits.Default },
        { nameof(XmlLimits.MaxAttributesPerElement), Attributes(XmlLimits.DefaultMaxAttributesPerElement + 1), XmlLimits.Default },
        { nameof(XmlLimits.MaxNamespaceDeclarationsPerElement), Attributes(XmlLimits.DefaultMaxNamespaceDeclarationsPerElement + 1, "xmlns"), XmlLimits.Default },
        { nameof(XmlLimits.MaxNamespaceDeclarations), "<r>" + string.Concat(Enumerable.Range(0, 11).Select(i => $"<x xmlns:p=\"urn:{i}\"/>")) + "</r>",
            XmlLimits.Default with { MaxNamespaceDeclarations = 10 } },
        { nameof(XmlLimits.MaxElements), "<r>" + string.Concat(Enumerable.Repeat("<x/>", 10)) + "</r>", XmlLimits.Default with { MaxElements = 10 } },
    };

    [Theory]
    [MemberData(nameof(OverALimit))]
    public void Every_loader_refuses_a_document_over_a_limit_and_names_it(string limit, string xml, XmlLimits limits)
    {
        var bytes = Bytes(xml);
        var loaders = new List<(string Name, Action Act)>
        {
            ("Load(byte[])", () => SafeXml.Load(bytes, limits)),
            ("Load(Stream)", () => SafeXml.Load(new MemoryStream(bytes), limits)),
            ("Parse(string)", () => SafeXml.Parse(xml, limits)),
            ("ParseElement(string)", () => SafeXml.ParseElement(xml, limits)),
            ("LoadDocument(byte[])", () => SafeXml.LoadDocument(bytes, limits)),
            ("Check(byte[])", () => SafeXml.Check(bytes, limits)),
        };

        foreach (var (name, act) in loaders)
            act.Should().Throw<XmlLimitException>(name).Which.Limit.Should().Be(limit, name);
    }

    [Fact]
    public void The_loaders_without_limits_apply_the_default_ones()
    {
        // Existing call sites (XPath over a message body, SOAP, route markup) get the structure limits without a change.
        var deep = Nested(XmlLimits.DefaultMaxDepth + 1);

        ((Action)(() => SafeXml.Parse(deep))).Should().Throw<XmlLimitException>().Which.Limit.Should().Be(nameof(XmlLimits.MaxDepth));
        ((Action)(() => SafeXml.LoadDocument(Bytes(deep)))).Should().Throw<XmlLimitException>();
        ((Action)(() => SafeXml.Load(Bytes(deep), maxCharacters: 10_000_000))).Should().Throw<XmlLimitException>();
    }

    [Fact]
    public void A_document_at_every_limit_reads()
    {
        SafeXml.Parse(Nested(XmlLimits.DefaultMaxDepth)).Descendants("x").Should().HaveCount(XmlLimits.DefaultMaxDepth);
        SafeXml.Parse(Attributes(XmlLimits.DefaultMaxAttributesPerElement)).Root!.Attributes().Should().HaveCount(XmlLimits.DefaultMaxAttributesPerElement);
        SafeXml.Parse(Attributes(XmlLimits.DefaultMaxNamespaceDeclarationsPerElement, "xmlns")).Root.Should().NotBeNull();
        SafeXml.Parse("<r><x/><x/></r>", XmlLimits.Default with { MaxElements = 3, MaxNamespaceDeclarations = 1 }).Root!.Elements().Should().HaveCount(2);
    }

    [Fact]
    public void A_limit_violation_is_an_xml_exception_with_its_position()
    {
        // Callers that answer malformed XML as the sender's fault (SOAP, AS4) answer this the same way, with the place.
        var act = () => SafeXml.Parse("<r>\n  <x/>\n  <x/>\n</r>", XmlLimits.Default with { MaxElements = 2 });

        var thrown = act.Should().Throw<XmlLimitException>().Which;
        thrown.Should().BeAssignableTo<XmlException>();
        thrown.LineNumber.Should().Be(3);
        thrown.Maximum.Should().Be(2);
        thrown.Message.Should().Contain("MaxElements");
    }

    [Fact]
    public void A_deep_document_is_refused_at_the_limit_not_after_reading_it_all()
    {
        // 200 000 levels: refused at level 257, so the time does not grow with the document.
        var watch = System.Diagnostics.Stopwatch.StartNew();
        ((Action)(() => SafeXml.Parse(Nested(200_000)))).Should().Throw<XmlLimitException>();
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void Line_info_and_whitespace_survive_the_limited_reader()
    {
        var doc = SafeXml.Parse("<a>\n  <b/>\n</a>", XmlLimits.Default, LoadOptions.SetLineInfo | LoadOptions.PreserveWhitespace);

        ((IXmlLineInfo)doc.Root!.Element("b")!).LineNumber.Should().Be(2);
        doc.Root.ToString(SaveOptions.DisableFormatting).Should().Contain("\n  <b />");
    }

    [Fact]
    public void A_limit_of_zero_is_refused_as_a_setting()
    {
        var act = () => SafeXml.Parse("<a/>", XmlLimits.Default with { MaxDepth = 0 });

        act.Should().Throw<ArgumentOutOfRangeException>().Which.ParamName.Should().Be(nameof(XmlLimits.MaxDepth));
    }
}

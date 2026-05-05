using System.Text;
using redb.Route.Expressions.Tokenizers;
using FluentAssertions;

namespace redb.Route.Tests.Expressions.Tokenizers;

public class XmlTokenizerTests
{
    private const string SimpleXml = "<orders><order>A</order><order>B</order></orders>";

    [Fact]
    public async Task String_TwoOrders_TwoElements()
    {
        var result = await Collect(XmlTokenizer.Tokenize(SimpleXml, "order", null)).ConfigureAwait(false);

        result.Should().HaveCount(2);
        result[0].Should().Be("<order>A</order>");
        result[1].Should().Be("<order>B</order>");
    }

    [Fact]
    public async Task Stream_TwoOrders_TwoElements()
    {
        var stream = new MemoryStream(Encoding.UTF8.GetBytes(SimpleXml));

        var result = await Collect(XmlTokenizer.Tokenize(stream, "order", null)).ConfigureAwait(false);

        result.Should().HaveCount(2);
    }

    [Fact]
    public async Task ByteArray_TwoOrders_TwoElements()
    {
        var bytes = Encoding.UTF8.GetBytes(SimpleXml);

        var result = await Collect(XmlTokenizer.Tokenize(bytes, "order", null)).ConfigureAwait(false);

        result.Should().HaveCount(2);
    }

    [Fact]
    public async Task InheritNamespace_InjectsParentNamespace()
    {
        const string xml = "<orders xmlns:ns=\"urn:test\"><order><id>1</id></order></orders>";

        var result = await Collect(XmlTokenizer.Tokenize(xml, "order", "orders")).ConfigureAwait(false);

        result.Should().ContainSingle();
        var orderXml = (string)result[0]!;
        orderXml.Should().Contain("xmlns:ns=\"urn:test\"");
    }

    [Fact]
    public async Task EmptyXml_NoOrders_ZeroElements()
    {
        const string xml = "<orders></orders>";

        var result = await Collect(XmlTokenizer.Tokenize(xml, "order", null)).ConfigureAwait(false);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task NestedSameElement_ReturnsOuterXml()
    {
        const string xml = "<root><item><item>nested</item></item></root>";

        var result = await Collect(XmlTokenizer.Tokenize(xml, "item", null)).ConfigureAwait(false);

        result.Should().ContainSingle();
        var outerXml = (string)result[0]!;
        outerXml.Should().Contain("nested");
    }

    [Fact]
    public async Task DtdProcessing_Prohibit_ThrowsOnDtd()
    {
        const string xml = "<!DOCTYPE foo [<!ENTITY xxe SYSTEM \"file:///etc/passwd\">]><root><item>&xxe;</item></root>";

        var act = async () => await Collect(XmlTokenizer.Tokenize(xml, "item", null)).ConfigureAwait(false);

        await act.Should().ThrowAsync<System.Xml.XmlException>().ConfigureAwait(false);
    }

    [Fact]
    public async Task UnsupportedBody_Throws()
    {
        var act = async () => await Collect(XmlTokenizer.Tokenize(12345, "item", null)).ConfigureAwait(false);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*unsupported body type*").ConfigureAwait(false);
    }

    private static async Task<List<object?>> Collect(IAsyncEnumerable<object?> source)
    {
        var list = new List<object?>();
        await foreach (var item in source.ConfigureAwait(false))
            list.Add(item);
        return list;
    }
}

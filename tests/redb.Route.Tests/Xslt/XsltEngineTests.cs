using System.Text;
using System.Xml;
using System.Xml.Linq;
using FluentAssertions;
using redb.Route.Xslt;

namespace redb.Route.Tests.Xslt;

/// <summary>Tests for <see cref="XslCompiledTransformEngine"/> and <see cref="XsltProcessor"/>.</summary>
public class XsltEngineTests
{
    // Maps <greeting><name>X</name></greeting> → <hello>X</hello>.
    private const string Stylesheet =
        """
        <xsl:stylesheet version="1.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
          <xsl:output method="xml" omit-xml-declaration="yes"/>
          <xsl:template match="/greeting"><hello><xsl:value-of select="name"/></hello></xsl:template>
        </xsl:stylesheet>
        """;

    private const string Input = "<greeting><name>world</name></greeting>";

    [Fact]
    public void FromContent_TransformsXml_ToString()
    {
        var engine = XslCompiledTransformEngine.FromContent(Stylesheet);

        var result = engine.Transform(Input, XsltOutput.String, null);

        result.Should().BeOfType<string>().Which.Should().Be("<hello>world</hello>");
    }

    [Fact]
    public void Transform_BytesOutput_ReturnsEncodedXml()
    {
        var engine = XslCompiledTransformEngine.FromContent(Stylesheet);

        var result = engine.Transform(Input, XsltOutput.Bytes, null);

        result.Should().BeOfType<byte[]>();
        Encoding.UTF8.GetString((byte[])result).Should().Contain("<hello>world</hello>");
    }

    [Fact]
    public void Transform_DomOutput_ReturnsXmlDocument()
    {
        var engine = XslCompiledTransformEngine.FromContent(Stylesheet);

        var result = engine.Transform(Input, XsltOutput.Dom, null);

        var doc = result.Should().BeOfType<XmlDocument>().Which;
        doc.DocumentElement!.Name.Should().Be("hello");
        doc.DocumentElement.InnerText.Should().Be("world");
    }

    [Fact]
    public void Transform_ByteArrayInput_Works()
    {
        var engine = XslCompiledTransformEngine.FromContent(Stylesheet);

        var result = engine.Transform(Encoding.UTF8.GetBytes(Input), XsltOutput.String, null);

        result.Should().Be("<hello>world</hello>");
    }

    [Fact]
    public void Transform_XDocumentInput_Works()
    {
        var engine = XslCompiledTransformEngine.FromContent(Stylesheet);

        var result = engine.Transform(XDocument.Parse(Input), XsltOutput.String, null);

        result.Should().Be("<hello>world</hello>");
    }

    [Fact]
    public void Transform_WithParameters_PassesToStylesheet()
    {
        const string paramStyle =
            """
            <xsl:stylesheet version="1.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:output method="text"/>
              <xsl:param name="who"/>
              <xsl:template match="/">Hello <xsl:value-of select="$who"/></xsl:template>
            </xsl:stylesheet>
            """;
        var engine = XslCompiledTransformEngine.FromContent(paramStyle);

        var result = engine.Transform("<x/>", XsltOutput.String,
            new Dictionary<string, object?> { ["who"] = "world" });

        result.Should().Be("Hello world");
    }

    [Fact]
    public void Transform_UnsupportedBodyType_Throws()
    {
        var engine = XslCompiledTransformEngine.FromContent(Stylesheet);

        var act = () => engine.Transform(42, XsltOutput.String, null);

        act.Should().Throw<NotSupportedException>();
    }

    [Fact]
    public void FromContent_InvalidStylesheet_Throws()
    {
        var act = () => XslCompiledTransformEngine.FromContent("<not-a-stylesheet/>");

        act.Should().Throw<Exception>();
    }

    [Fact]
    public void FromContent_NullOrEmpty_Throws()
    {
        var act = () => XslCompiledTransformEngine.FromContent("");
        act.Should().Throw<ArgumentException>();
    }
}

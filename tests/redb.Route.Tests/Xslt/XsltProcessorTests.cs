using FluentAssertions;
using redb.Route.Core;
using redb.Route.Xslt;

namespace redb.Route.Tests.Xslt;

/// <summary>Tests for <see cref="XsltProcessor"/> body/null handling.</summary>
public class XsltProcessorTests
{
    private const string Stylesheet =
        """
        <xsl:stylesheet version="1.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
          <xsl:output method="xml" omit-xml-declaration="yes"/>
          <xsl:template match="/greeting"><hello><xsl:value-of select="name"/></hello></xsl:template>
        </xsl:stylesheet>
        """;

    private static XsltProcessor NewProcessor(bool failOnNullBody = true)
        => new(XslCompiledTransformEngine.FromContent(Stylesheet), XsltOutput.String, failOnNullBody);

    [Fact]
    public async Task Process_TransformsBody_InPlace()
    {
        var exchange = new Exchange(new Message("<greeting><name>world</name></greeting>"));

        await NewProcessor().Process(exchange);

        exchange.In.Body.Should().Be("<hello>world</hello>");
    }

    [Fact]
    public async Task Process_NullBody_ThrowsWhenFailOnNull()
    {
        var exchange = new Exchange(new Message(null));

        var act = () => NewProcessor(failOnNullBody: true).Process(exchange);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Process_NullBody_PassesThroughWhenNotFailOnNull()
    {
        var exchange = new Exchange(new Message(null));

        await NewProcessor(failOnNullBody: false).Process(exchange);

        exchange.In.Body.Should().BeNull();
    }

    [Fact]
    public void Constructor_NullEngine_Throws()
    {
        var act = () => new XsltProcessor(null!);
        act.Should().Throw<ArgumentNullException>().WithParameterName("engine");
    }
}

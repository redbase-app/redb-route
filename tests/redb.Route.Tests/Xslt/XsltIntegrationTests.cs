using FluentAssertions;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Xslt;

namespace redb.Route.Tests.Xslt;

/// <summary>
/// Integration tests for XSLT through the DSL → Compiler → Engine pipeline, covering the inline
/// <c>.XsltContent(...)</c> and file-based <c>.Xslt(...)</c> leaf forms and the <c>xslt:</c> component.
/// </summary>
public class XsltIntegrationTests : IAsyncDisposable
{
    private const string Stylesheet =
        """
        <xsl:stylesheet version="1.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
          <xsl:output method="xml" omit-xml-declaration="yes"/>
          <xsl:template match="/greeting"><hello><xsl:value-of select="name"/></hello></xsl:template>
        </xsl:stylesheet>
        """;

    // Alternate stylesheet used for the dynamic (from-header) test: greeting → <bye>.
    private const string AltStylesheet =
        """
        <xsl:stylesheet version="1.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
          <xsl:output method="xml" omit-xml-declaration="yes"/>
          <xsl:template match="/greeting"><bye><xsl:value-of select="name"/></bye></xsl:template>
        </xsl:stylesheet>
        """;

    // Stylesheet that reads a parameter fed from a message header.
    private const string ParamStylesheet =
        """
        <xsl:stylesheet version="1.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
          <xsl:output method="text"/>
          <xsl:param name="region"/>
          <xsl:template match="/">region=<xsl:value-of select="$region"/></xsl:template>
        </xsl:stylesheet>
        """;

    private const string Input = "<greeting><name>world</name></greeting>";
    private const string Expected = "<hello>world</hello>";

    private readonly RouteContext _context = new();
    private readonly string _stylesheetFile;
    private readonly string _altStylesheetFile;

    public XsltIntegrationTests()
    {
        _stylesheetFile = Path.Combine(Path.GetTempPath(), $"redb-xslt-{Guid.NewGuid():N}.xsl");
        File.WriteAllText(_stylesheetFile, Stylesheet);
        _altStylesheetFile = Path.Combine(Path.GetTempPath(), $"redb-xslt-alt-{Guid.NewGuid():N}.xsl");
        File.WriteAllText(_altStylesheetFile, AltStylesheet);
    }

    public async ValueTask DisposeAsync()
    {
        await _context.DisposeAsync();
        try { File.Delete(_stylesheetFile); } catch { /* best-effort */ }
        try { File.Delete(_altStylesheetFile); } catch { /* best-effort */ }
        GC.SuppressFinalize(this);
    }

    private async Task<IProducer> StartAndGetProducer(string fromUri)
    {
        await _context.Start();
        var producer = _context.GetEndpoint(fromUri).CreateProducer();
        await producer.Start();
        return producer;
    }

    [Fact]
    public async Task Xslt_InlineContent_TransformsBody()
    {
        object? result = null;
        _context.AddRoutes(r =>
            r.From("direct://xslt-content")
                .XsltContent(Stylesheet)
                .Process(e => result = e.In.Body));

        var producer = await StartAndGetProducer("direct://xslt-content");
        await producer.Process(new Exchange(new Message(Input)));

        result.Should().Be(Expected);
    }

    [Fact]
    public async Task Xslt_FromFile_TransformsBody()
    {
        object? result = null;
        _context.AddRoutes(r =>
            r.From("direct://xslt-file")
                .Xslt(_stylesheetFile)
                .Process(e => result = e.In.Body));

        var producer = await StartAndGetProducer("direct://xslt-file");
        await producer.Process(new Exchange(new Message(Input)));

        result.Should().Be(Expected);
    }

    [Fact]
    public async Task Xslt_Component_ViaTo_TransformsBody()
    {
        // The xslt: component is registered out of the box — no AddComponent needed.
        object? result = null;
        _context.AddRoutes(r =>
            r.From("direct://xslt-comp")
                .To($"xslt:{_stylesheetFile}")
                .Process(e => result = e.In.Body));

        var producer = await StartAndGetProducer("direct://xslt-comp");
        await producer.Process(new Exchange(new Message(Input)));

        result.Should().Be(Expected);
    }

    [Fact]
    public async Task Xslt_Component_BytesOutput()
    {
        object? result = null;
        _context.AddRoutes(r =>
            r.From("direct://xslt-bytes")
                .To($"xslt:{_stylesheetFile}?output=bytes")
                .Process(e => result = e.In.Body));

        var producer = await StartAndGetProducer("direct://xslt-bytes");
        await producer.Process(new Exchange(new Message(Input)));

        result.Should().BeOfType<byte[]>();
        System.Text.Encoding.UTF8.GetString((byte[])result!).Should().Contain(Expected);
    }

    [Fact]
    public async Task Xslt_PassesHeadersAsParameters()
    {
        object? result = null;
        _context.AddRoutes(r =>
            r.From("direct://xslt-param")
                .XsltContent(ParamStylesheet)
                .Process(e => result = e.In.Body));

        var producer = await StartAndGetProducer("direct://xslt-param");
        var exchange = new Exchange(new Message("<x/>"));
        exchange.In.Headers["region"] = "EU";           // fed to <xsl:param name="region"/>
        await producer.Process(exchange);

        result.Should().Be("region=EU");
    }

    [Fact]
    public async Task Xslt_AllowTemplateFromHeader_OverridesStylesheetPerMessage()
    {
        object? result = null;
        _context.AddRoutes(r =>
            r.From("direct://xslt-dyn")
                .Xslt(_stylesheetFile, allowTemplateFromHeader: true)   // default → <hello>
                .Process(e => result = e.In.Body));

        var producer = await StartAndGetProducer("direct://xslt-dyn");
        var exchange = new Exchange(new Message(Input));
        exchange.In.Headers[XsltHeaders.ResourceUri] = _altStylesheetFile; // override → <bye>
        await producer.Process(exchange);

        result.Should().Be("<bye>world</bye>");
    }
}

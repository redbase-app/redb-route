using System.Text;
using AwesomeAssertions;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Xml;

namespace redb.Route.Tests.Xml;

/// <summary>
/// <c>&lt;addAttachment&gt;</c>, <c>&lt;removeAttachment&gt;</c> and <c>&lt;removeAttachments&gt;</c>: a markup route
/// builds a message with files without code, from an expression, the body or a file.
/// </summary>
public class AttachmentElementsTests : IAsyncLifetime
{
    private readonly RouteContext _context = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _context.DisposeAsync();

    private async Task<IExchange> Run(string steps, string body = "the body")
    {
        _context.AddXmlRoutesFromContent($"""
            <routes xmlns="urn:redb:route:1.0">
              <route id="att">
                <from uri="direct://att-in"/>
                {steps}
              </route>
            </routes>
            """, "attachments.xml");
        await _context.Start();
        var producer = _context.GetEndpoint("direct://att-in").CreateProducer();
        await producer.Start();
        var exchange = new Exchange(new Message(body));
        await producer.Process(exchange);
        return exchange;
    }

    private static string Text(Attachment a) => Encoding.UTF8.GetString(a.Content.ReadAllBytes());

    [Fact]
    public async Task The_three_sources_add_attachments_and_remove_takes_them_off()
    {
        var path = Path.Combine(Path.GetTempPath(), $"xml-att-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(path, "file text");
        try
        {
            var exchange = await Run($"""
                <setHeader name="who" value="anna"/>
                <setHeader name="path" value="{path}"/>
                <addAttachment name="note" expr="${"{"}header.who{"}"}" fileNameExpr="${"{"}header.who{"}"}.txt"/>
                <addAttachment name="copy.bin" fromBody="true" fileName="copy.bin" contentType="application/pdf"/>
                <addAttachment file="{path}"/>
                <addAttachment name="by-header" fileExpr="${"{"}header.path{"}"}"/>
                <addAttachment name="gone" expr="'x'"/>
                <removeAttachment name="gone"/>
                """);

            var attachments = exchange.In.Attachments;
            attachments.Select(a => a.Name).Should().Equal("note", "copy.bin", Path.GetFileName(path), "by-header");
            Text(attachments[0]).Should().Be("anna");
            attachments[0].FileName.Should().Be("anna.txt");
            Text(attachments[1]).Should().Be("the body");
            attachments[1].ContentType.Should().Be("application/pdf");
            Text(attachments[2]).Should().Be("file text");
            Text(attachments[3]).Should().Be("file text");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task RemoveAttachments_clears_them()
    {
        var exchange = await Run("""
            <addAttachment name="a" expr="'1'"/>
            <removeAttachments/>
            """);

        exchange.In.Attachments.Should().BeEmpty();
    }

    [Fact]
    public async Task Two_sources_on_one_element_are_a_markup_error()
    {
        var act = () => Run("""<addAttachment name="a" expr="'1'" fromBody="true"/>""");

        await act.Should().ThrowAsync<XmlRouteException>().WithMessage("*attachments.xml(*exactly one of 'expr', 'fromBody', 'file' or 'fileExpr'*");
    }

    [Fact]
    public void Generated_code_names_the_optional_arguments_so_the_overload_is_never_ambiguous()
    {
        var document = System.Xml.Linq.XDocument.Parse("""
            <routes xmlns="urn:redb:route:1.0">
              <route id="att-gen">
                <from uri="direct://in"/>
                <addAttachment name="note" expr="${header.n}" contentType="text/plain"/>
                <addAttachment name="body" fromBody="true" fileNameExpr="${header.f}"/>
                <addAttachment fileExpr="${header.p}" name="doc"/>
                <removeAttachments/>
              </route>
            </routes>
            """);

        var code = XmlCodeGenerator.Generate(document, "AttachmentsGenerated", "Tests.Generated");

        code.Should().Contain("AddAttachment(\"note\", Expr(\"${header.n}\"), contentType: \"text/plain\")");
        code.Should().Contain("AddAttachmentFromBody(\"body\", fileName: Expr(\"${header.f}\"))");
        code.Should().Contain("AddAttachmentFromFile(Expr(\"${header.p}\"), name: \"doc\")");
        code.Should().Contain("RemoveAttachments()");
    }

    [Theory]
    [InlineData("", 2)]
    [InlineData(" maxParts=\"1\"", -1)]
    public async Task A_MIME_multipart_format_declared_in_context_xml_reads_a_form_with_its_options(string options, int expectedFiles)
    {
        var type = "redb.Route.DataFormats.Mime.MimeMultipartDataFormat, redb.Route.DataFormats.Mime";
        new XmlContextLoader(_context).Load(System.Xml.Linq.XDocument.Parse(
            $"""<context xmlns="urn:redb:route:1.0"><dataFormat type="{type}"{options}/></context>""",
            System.Xml.Linq.LoadOptions.SetLineInfo), "context.xml");
        _context.AddXmlRoutesFromContent("""
            <routes xmlns="urn:redb:route:1.0">
              <route id="form">
                <from uri="direct://form-in"/>
                <convertBody type="redb.Route.Serialization.FormBody, redb.Route"/>
              </route>
            </routes>
            """, "form.xml");
        await _context.Start();
        var producer = _context.GetEndpoint("direct://form-in").CreateProducer();
        await producer.Start();
        var body = Encoding.UTF8.GetBytes(
            "--b\r\nContent-Disposition: form-data; name=\"f\"; filename=\"a.txt\"\r\n\r\nA\r\n" +
            "--b\r\nContent-Disposition: form-data; name=\"f\"; filename=\"b.txt\"\r\n\r\nB\r\n--b--\r\n");
        var exchange = new Exchange(new Message(body) { ContentType = "multipart/form-data; boundary=b" });

        var act = () => producer.Process(exchange);

        if (expectedFiles < 0)
        {
            await act.Should().ThrowAsync<InvalidDataException>().WithMessage("*MaxParts*", "the attribute reached the options");
            return;
        }
        await act();
        exchange.In.Attachments.Select(a => a.Name).Should().Equal("a.txt", "b.txt");
    }

    [Fact]
    public async Task An_attachment_without_a_name_is_a_markup_error()
    {
        var act = () => Run("""<addAttachment expr="'1'"/>""");

        await act.Should().ThrowAsync<XmlRouteException>().WithMessage("*requires the 'name' attribute*");
    }
}

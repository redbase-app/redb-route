using System.Net.Http.Headers;
using System.Text;
using redb.Route.Abstractions;
using redb.Route.Components;
using redb.Route.Core;
using redb.Route.DataFormats.Mime;
using redb.Route.Serialization;

namespace redb.Route.Tests.DataFormats;

/// <summary>
/// MIME multipart: a form posted with files reads as a <see cref="FormBody"/> plus attachments, any other multipart the
/// Camel way, and both write back. The bodies are the ones real senders produce: a browser (raw UTF-8 file names) and
/// .NET's <see cref="MultipartFormDataContent"/>.
/// </summary>
public class MimeMultipartDataFormatTests
{
    private static readonly byte[] Pdf = [0x25, 0x50, 0x44, 0x46, 0x00, 0xFF, 0x0D, 0x0A, 0x2D, 0x2D, 0x00, 0xFE];
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x0D, 0x0A, 0x0D, 0x0A, 0xFF, 0xD9];

    private static Exchange Received(byte[] body, string? contentType) =>
        new(new Message(body) { ContentType = contentType });

    private static async Task<Exchange> Unmarshal(byte[] body, string? contentType, Type? target = null, MimeMultipartDataFormatOptions? options = null)
    {
        var exchange = Received(body, contentType);
        await new MimeMultipartDataFormat(options).UnmarshalAsync(exchange, target ?? typeof(FormBody));
        return exchange;
    }

    /// <summary>What Chrome sends for a form with two fields and two files: raw UTF-8 in the file name, binary parts.</summary>
    private static byte[] BrowserForm(out string contentType)
    {
        const string boundary = "----WebKitFormBoundary7MA4YWxkTrZu0gW";
        contentType = $"multipart/form-data; boundary={boundary}";
        var body = new MemoryStream();
        void Text(string s) => body.Write(Encoding.UTF8.GetBytes(s));
        Text($"--{boundary}\r\nContent-Disposition: form-data; name=\"claim\"\r\n\r\n12345\r\n");
        Text($"--{boundary}\r\nContent-Disposition: form-data; name=\"theme\"\r\n\r\nпретензия по качеству\r\n");
        Text($"--{boundary}\r\nContent-Disposition: form-data; name=\"docs\"; filename=\"акт.pdf\"\r\nContent-Type: application/pdf\r\n\r\n");
        body.Write(Pdf);
        Text($"\r\n--{boundary}\r\nContent-Disposition: form-data; name=\"docs\"; filename=\"C:\\Users\\anna\\photo.jpg\"\r\nContent-Type: image/jpeg\r\n\r\n");
        body.Write(Jpeg);
        Text($"\r\n--{boundary}--\r\n");
        return body.ToArray();
    }

    private static byte[] Bytes(Attachment attachment) => attachment.Content.ReadAllBytes();

    // ── multipart/form-data ──

    [Fact]
    public async Task A_browser_form_reads_as_fields_and_file_attachments_byte_for_byte()
    {
        var exchange = await Unmarshal(BrowserForm(out var contentType), contentType);

        var form = exchange.In.Body.Should().BeOfType<FormBody>().Subject;
        form.Get("claim").Should().Be("12345");
        form.Get("theme").Should().Be("претензия по качеству");

        var files = exchange.In.Attachments;
        files.Select(f => f.Name).Should().Equal("акт.pdf", "photo.jpg");
        files[0].ContentType.Should().Be("application/pdf");
        Bytes(files[0]).Should().Equal(Pdf, "bytes with CR, LF, '--', 0x00 and 0xFF survive");
        files[1].FileName.Should().Be("photo.jpg", "the directory an old client sent is dropped");
        Bytes(files[1]).Should().Equal(Jpeg);
        files[0].Headers["Content-Disposition"].Should().Contain("name=\"docs\"", "the form field stays readable");

        exchange.In.ContentType.Should().BeNull("the boundary no longer describes the body");
    }

    [Fact]
    public async Task A_form_written_by_a_dotnet_client_reads_the_same()
    {
        using var content = new MultipartFormDataContent();
        content.Add(new StringContent("7"), "claim");
        content.Add(new StringContent("a"), "tag");
        content.Add(new StringContent("b"), "tag");
        var file = new ByteArrayContent(Pdf);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        content.Add(file, "docs", "report.pdf");

        var exchange = await Unmarshal(await content.ReadAsByteArrayAsync(), content.Headers.ContentType!.ToString());

        var form = (FormBody)exchange.In.Body!;
        form.Get("claim").Should().Be("7");
        form.GetAll("tag").Should().Equal("a", "b");
        Bytes(exchange.In.Attachments.Single()).Should().Equal(Pdf);
        exchange.In.Attachments[0].Name.Should().Be("report.pdf");
    }

    [Fact]
    public async Task A_body_that_is_not_multipart_is_left_as_it_is()
    {
        var exchange = await Unmarshal("{\"a\":1}"u8.ToArray(), "application/json");

        exchange.In.Body.Should().BeOfType<byte[]>();
        exchange.In.ContentType.Should().Be("application/json");
        exchange.In.Attachments.Should().BeEmpty();
    }

    [Fact]
    public async Task Without_a_boundary_parameter_the_first_line_names_it()
    {
        var body = BrowserForm(out _);

        var exchange = await Unmarshal(body, "multipart/form-data");

        ((FormBody)exchange.In.Body!).Get("claim").Should().Be("12345");
        exchange.In.Attachments.Should().HaveCount(2);
    }

    [Fact]
    public async Task The_content_type_header_counts_when_the_message_has_none()
    {
        var exchange = Received(BrowserForm(out var contentType), null);
        exchange.In.Headers["Content-Type"] = contentType;

        await new MimeMultipartDataFormat().UnmarshalAsync(exchange, typeof(FormBody));

        exchange.In.Attachments.Should().HaveCount(2);
        exchange.In.Headers.ContainsKey("Content-Type").Should().BeFalse();
    }

    [Fact]
    public async Task A_stream_body_is_read_without_buffering_by_the_step()
    {
        var exchange = new Exchange(new Message(new MemoryStream(BrowserForm(out var contentType))) { ContentType = contentType });

        await new MimeMultipartDataFormat().UnmarshalAsync(exchange, typeof(FormBody));

        exchange.In.Attachments.Should().HaveCount(2);
    }

    // ── Limits ──

    [Fact]
    public async Task Limits_fail_the_step_with_the_limit_named()
    {
        var body = BrowserForm(out var contentType);

        var parts = () => Unmarshal(body, contentType, options: new() { MaxParts = 3 });
        var part = () => Unmarshal(body, contentType, options: new() { MaxPartBytes = 8 });
        var total = () => Unmarshal(body, contentType, options: new() { MaxTotalBytes = 100 });
        var name = () => Unmarshal(body, contentType, options: new() { MaxFileNameLength = 5 });

        await parts.Should().ThrowAsync<InvalidDataException>().WithMessage("*MaxParts*");
        await part.Should().ThrowAsync<InvalidDataException>().WithMessage("*MaxPartBytes*");
        await total.Should().ThrowAsync<InvalidDataException>().WithMessage("*MaxTotalBytes*");
        await name.Should().ThrowAsync<InvalidDataException>().WithMessage("*MaxFileNameLength*");
    }

    [Fact]
    public async Task A_field_without_a_name_is_refused()
    {
        var body = "--b\r\nContent-Disposition: form-data\r\n\r\nx\r\n--b--\r\n"u8.ToArray();

        var act = () => Unmarshal(body, "multipart/form-data; boundary=b");

        await act.Should().ThrowAsync<InvalidDataException>().WithMessage("*has no name*");
    }

    [Fact]
    public async Task Another_target_type_for_a_form_is_refused()
    {
        var act = () => Unmarshal(BrowserForm(out var contentType), contentType, typeof(int));

        await act.Should().ThrowAsync<NotSupportedException>().WithMessage("*FormBody*");
    }

    // ── multipart/mixed, the Camel way ──

    private const string Mixed =
        "--m\r\nContent-Type: text/plain; charset=utf-8\r\n\r\nпривет\r\n" +
        "--m\r\nContent-Type: application/octet-stream\r\nContent-Disposition: attachment; filename=\"a.bin\"\r\nContent-Transfer-Encoding: base64\r\n\r\nAAH/\r\n" +
        "--m\r\nContent-Type: image/png\r\nContent-ID: <logo@example>\r\n\r\npng\r\n" +
        "--m\r\nContent-Type: text/plain\r\n\r\nno name\r\n" +
        "--m--\r\n";

    [Fact]
    public async Task Mixed_puts_the_first_part_in_the_body_and_names_the_rest_as_Camel_does()
    {
        var exchange = await Unmarshal(Encoding.UTF8.GetBytes(Mixed), "multipart/mixed; boundary=m", typeof(string));

        exchange.In.Body.Should().Be("привет");
        exchange.In.ContentType.Should().Be("text/plain; charset=utf-8");
        var attachments = exchange.In.Attachments;
        attachments[0].Name.Should().Be("a.bin", "the file name first");
        Bytes(attachments[0]).Should().Equal([0x00, 0x01, 0xFF], "base64 is decoded");
        attachments[1].Name.Should().Be("logo@example", "then the Content-ID");
        attachments[1].ContentId.Should().Be("logo@example");
        attachments[2].Name.Should().EndWith("@redb", "then a generated id");
    }

    [Fact]
    public async Task Mixed_to_a_stream_keeps_the_first_part_re_readable()
    {
        var exchange = await Unmarshal(Encoding.UTF8.GetBytes(Mixed), "multipart/mixed; boundary=m", typeof(object));

        var body = exchange.In.Body.Should().BeOfType<StreamCache>().Subject;
        new StreamReader(body).ReadToEnd().Should().Be("привет");
    }

    // ── Marshal ──

    [Fact]
    public async Task A_form_and_attachments_write_a_form_that_reads_back_the_same()
    {
        var exchange = new Exchange(new Message(new FormBody().Add("claim", "7").Add("theme", "претензия")));
        exchange.In.Attachments.Add(new Attachment
        {
            Name = "акт.pdf", FileName = "акт.pdf", ContentType = "application/pdf", Content = AttachmentContent.FromBytes(Pdf)
        });

        await new MimeMultipartDataFormat().MarshalAsync(exchange);

        var contentType = exchange.In.ContentType!;
        contentType.Should().StartWith("multipart/form-data; boundary=");
        exchange.In.Headers["Content-Type"].Should().Be(contentType, "http: takes the header first");
        var wire = ReadAll((Stream)exchange.In.Body!);
        Encoding.UTF8.GetString(wire).Should().Contain("filename=\"акт.pdf\"", "a browser-style UTF-8 file name, not RFC 2231");

        var back = await Unmarshal(wire, contentType);
        ((FormBody)back.In.Body!).Get("theme").Should().Be("претензия");
        Bytes(back.In.Attachments.Single()).Should().Equal(Pdf);
        back.In.Attachments[0].Name.Should().Be("акт.pdf");
    }

    [Fact]
    public async Task Every_marshal_gets_a_new_boundary()
    {
        async Task<string> Once()
        {
            var exchange = new Exchange(new Message(new FormBody().Add("a", "1")));
            await new MimeMultipartDataFormat().MarshalAsync(exchange);
            return exchange.In.ContentType!;
        }

        (await Once()).Should().NotBe(await Once());
    }

    [Fact]
    public async Task A_file_read_from_a_form_goes_back_under_its_form_field()
    {
        var exchange = await Unmarshal(BrowserForm(out var contentType), contentType);

        await new MimeMultipartDataFormat().MarshalAsync(exchange);

        var wire = Encoding.UTF8.GetString(ReadAll((Stream)exchange.In.Body!));
        wire.Should().Contain("name=\"docs\"; filename=\"акт.pdf\"").And.Contain("name=\"claim\"");
    }

    [Fact]
    public async Task Mixed_writes_the_body_first_and_skips_a_message_without_attachments()
    {
        var format = new MimeMultipartDataFormat(new() { SubType = "mixed" });

        var plain = new Exchange(new Message("just text"));
        await format.MarshalAsync(plain);
        plain.In.Body.Should().Be("just text", "without attachments the body is left as it is, as Camel does");

        var withFile = new Exchange(new Message("body text") { ContentType = "text/plain; charset=utf-8" });
        withFile.In.Attachments.Add(new Attachment { Name = "a.bin", FileName = "a.bin", Content = AttachmentContent.FromBytes([0, 1, 255]) });
        await format.MarshalAsync(withFile);

        var back = await Unmarshal(ReadAll((Stream)withFile.In.Body!), withFile.In.ContentType, typeof(string));
        back.In.Body.Should().Be("body text");
        Bytes(back.In.Attachments.Single()).Should().Equal([0, 1, 255]);
    }

    [Fact]
    public void The_byte_methods_say_the_format_needs_the_message()
    {
        var act = () => new MimeMultipartDataFormat().Deserialize<FormBody>([1]);

        act.Should().Throw<NotSupportedException>().WithMessage("*whole message*");
    }

    // ── In a route ──

    [Fact]
    public async Task ConvertBody_reads_a_form_through_the_registered_format()
    {
        await using var context = new RouteContext($"mime-{Guid.NewGuid():N}");
        context.AddComponent(new DirectComponent());
        context.AddMimeMultipartDataFormat();
        IExchange? seen = null;
        context.AddRoutes(r => r.From("direct://mime-in")
            .ConvertBody<FormBody>()
            .Filter("attachments.size == 2")
                .Process(e => seen = e)
            .End());
        await context.Start();
        var template = new ProducerTemplate(context);
        template.Start();

        await template.SendAsync("direct://mime-in", new Message(BrowserForm(out var contentType)) { ContentType = contentType });

        seen.Should().NotBeNull();
        ((FormBody)seen!.In.Body!).Get("claim").Should().Be("12345");
    }

    private static byte[] ReadAll(Stream stream)
    {
        stream.Position = 0;
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        stream.Position = 0;
        return buffer.ToArray();
    }
}

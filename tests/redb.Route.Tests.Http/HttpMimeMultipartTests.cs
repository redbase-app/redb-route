using System.Net;
using AwesomeAssertions;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.DataFormats.Mime;
using redb.Route.Http;
using redb.Route.Serialization;

namespace redb.Route.Tests.Http;

/// <summary>
/// A form with files over real HTTP: posted by <see cref="HttpClient"/> to an <c>http:</c> consumer and read by the MIME
/// multipart format, and a form the format writes read by ASP.NET Core's own multipart reader, so the wire matches what
/// both sides of the ecosystem produce and accept.
/// </summary>
[Collection("HttpServer")]
public class HttpMimeMultipartTests
{
    private static readonly byte[] Pdf = [0x25, 0x50, 0x44, 0x46, 0x00, 0xFF, 0x0D, 0x0A, 0x2D, 0x2D, 0x00];
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0x00, 0x0D, 0x0A, 0xFF, 0xD9];

    [Fact]
    public async Task A_form_posted_over_http_reads_as_two_fields_and_two_files_byte_for_byte()
    {
        var port = global::redb.Route.Tests.Shared.TestPorts.Next();
        await using var context = new RouteContext($"http-mime-{Guid.NewGuid():N}");
        context.AddComponent(new HttpComponent { ServerManager = new SharedHttpServerManager() });
        context.AddMimeMultipartDataFormat();
        // Read while the exchange is alive: a spooled attachment is released with it, like a stream body.
        var seen = new TaskCompletionSource<(FormBody Fields, List<(string Name, string Type, byte[] Bytes)> Files)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        context.AddRoutes(r => r.From($"http://127.0.0.1:{port}/claims")
            .ConvertBody<FormBody>()
            .Process(e => seen.TrySetResult(((FormBody)e.In.Body!,
                e.In.Attachments.Select(a => (a.Name, a.ContentType, a.Content.ReadAllBytes())).ToList()))));
        await context.Start();

        using var client = new HttpClient();
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("12345"), "claim");
        form.Add(new StringContent("претензия"), "theme");
        form.Add(File(Pdf, "application/pdf"), "docs", "акт.pdf");
        form.Add(File(Jpeg, "image/jpeg"), "docs", "photo.jpg");
        var response = await client.PostAsync($"http://127.0.0.1:{port}/claims", form);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var (fields, files) = await seen.Task.WaitAsync(TimeSpan.FromSeconds(10));
        fields.Get("claim").Should().Be("12345");
        fields.Get("theme").Should().Be("претензия");
        files.Select(f => f.Name).Should().Equal("акт.pdf", "photo.jpg");
        files[0].Type.Should().Be("application/pdf");
        files[0].Bytes.Should().Equal(Pdf);
        files[1].Bytes.Should().Equal(Jpeg);
    }

    [Fact]
    public async Task A_form_the_format_writes_is_read_by_ASP_NET_Core()
    {
        var exchange = new Exchange(new Message(new FormBody().Add("claim", "7").Add("theme", "претензия")));
        exchange.In.Attachments.Add(new Attachment
        {
            Name = "акт.pdf", FileName = "акт.pdf", ContentType = "application/pdf", Content = AttachmentContent.FromBytes(Pdf)
        });
        await new MimeMultipartDataFormat().MarshalAsync(exchange);

        var boundary = HeaderUtilities.RemoveQuotes(
            Microsoft.Net.Http.Headers.MediaTypeHeaderValue.Parse(exchange.In.ContentType).Boundary).Value!;
        var body = (Stream)exchange.In.Body!;
        body.Position = 0;
        var reader = new MultipartReader(boundary, body);

        var parts = new List<(string? Name, string? FileName, byte[] Content)>();
        while (await reader.ReadNextSectionAsync() is { } section)
        {
            ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var disposition).Should().BeTrue();
            using var content = new MemoryStream();
            await section.Body.CopyToAsync(content);
            parts.Add((HeaderUtilities.RemoveQuotes(disposition!.Name).Value,
                       HeaderUtilities.RemoveQuotes(disposition.FileName).Value,
                       content.ToArray()));
        }

        parts.Select(p => p.Name).Should().Equal("claim", "theme", "акт.pdf");
        System.Text.Encoding.UTF8.GetString(parts[1].Content).Should().Be("претензия");
        parts[2].FileName.Should().Be("акт.pdf");
        parts[2].Content.Should().Equal(Pdf);
    }

    private static ByteArrayContent File(byte[] bytes, string type)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(type);
        return content;
    }
}

using System.Net;
using System.Net.Sockets;
using System.Text;
using FluentAssertions;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Soap;
using SoapDsl = redb.Route.Soap.Fluent.Soap;

namespace redb.Route.Tests.Soap;

/// <summary>
/// Ф6 MTOM/XOP (camel-cxf <c>mtom-enabled</c> parity): binary attachments as multipart/related, exposed on
/// the <see cref="SoapHeaders.Attachments"/> plane. Unit round-trip of the packer plus a full producer→consumer
/// loopback carrying attachments in both directions.
/// </summary>
public class SoapMtomTests
{
    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    [Fact]
    public void Multipart_Write_Then_Parse_RoundTrips()
    {
        var payload = new byte[] { 1, 2, 3, 4, 5, 250, 251, 252 };
        var atts = new List<SoapAttachment> { new("blob-1", "application/octet-stream", payload) };
        var root = Encoding.UTF8.GetBytes("<soap:Envelope xmlns:soap=\"http://schemas.xmlsoap.org/soap/envelope/\"/>");

        var (body, contentType) = SoapMultipart.Write(root, "text/xml; charset=utf-8", atts);
        contentType.Should().Contain("multipart/related").And.Contain("application/xop+xml").And.Contain("start-info=\"text/xml\"");

        var (parsedRoot, parsedAtts) = SoapMultipart.Parse(body, contentType);
        Encoding.UTF8.GetString(parsedRoot).Should().Contain("Envelope");
        parsedAtts.Should().ContainSingle();
        parsedAtts[0].ContentId.Should().Be("blob-1");
        parsedAtts[0].Content.Should().Equal(payload);
    }

    // Builds a raw multipart/related body with a chosen (short) boundary, so we can exercise the parser's
    // robustness against foreign wire shapes the GUID-boundary writer never produces.
    private static byte[] Raw(string s) => Encoding.ASCII.GetBytes(s.Replace("\n", "\r\n"));

    [Fact]
    public void Parse_DoesNotSplit_OnBoundaryBytesInsideAttachmentContent()
    {
        // The attachment body literally contains "--BND", but NOT at a line start — it must survive intact.
        const string ct = "multipart/related; boundary=BND; type=\"application/xop+xml\"; start=\"<root>\"";
        var body = Raw(
            "--BND\n" +
            "Content-Type: application/xop+xml; type=\"text/xml\"\n" +
            "Content-ID: <root>\n" +
            "\n" +
            "<Envelope/>\n" +
            "--BND\n" +
            "Content-Type: application/octet-stream\n" +
            "Content-ID: <a1>\n" +
            "\n" +
            "XX--BNDYY\n" +          // boundary bytes mid-content, not line-anchored
            "--BND--\n");

        var (root, atts) = SoapMultipart.Parse(body, ct);
        Encoding.ASCII.GetString(root).Should().Contain("Envelope");
        atts.Should().ContainSingle();
        Encoding.ASCII.GetString(atts[0].Content).Should().Be("XX--BNDYY");
    }

    [Fact]
    public void Parse_KeepsAllAttachments_WhenRootPartHasNoContentId()
    {
        // Root part is plain text/xml with no Content-ID and no `start` param → fallback picks the first part,
        // and BOTH attachments must survive (regression: the old fallback dropped the first attachment).
        const string ct = "multipart/related; boundary=BND";
        var body = Raw(
            "--BND\n" +
            "Content-Type: text/xml\n" +
            "\n" +
            "<Envelope/>\n" +
            "--BND\n" +
            "Content-Type: application/octet-stream\n" +
            "Content-ID: <a1>\n" +
            "\n" +
            "AAA\n" +
            "--BND\n" +
            "Content-Type: application/octet-stream\n" +
            "Content-ID: <b1>\n" +
            "\n" +
            "BBB\n" +
            "--BND--\n");

        var (root, atts) = SoapMultipart.Parse(body, ct);
        Encoding.ASCII.GetString(root).Should().Contain("Envelope");
        atts.Select(a => a.ContentId).Should().BeEquivalentTo(new[] { "a1", "b1" });
        atts.Select(a => Encoding.ASCII.GetString(a.Content)).Should().BeEquivalentTo(new[] { "AAA", "BBB" });
    }

    [Fact]
    public void Parse_Throws_OnMultipartContentTypeButNoDelimiter()
    {
        const string ct = "multipart/related; boundary=BND";
        var body = Encoding.ASCII.GetBytes("<Envelope/> plain, no boundary here");
        var act = () => SoapMultipart.Parse(body, ct);
        act.Should().Throw<FormatException>();
    }

    [Fact]
    public async Task Mtom_Loopback_CarriesAttachmentsBothWays()
    {
        var port = FreePort();
        byte[]? consumerGot = null;
        var uploaded = Encoding.UTF8.GetBytes("the-uploaded-bytes");
        var replied = Encoding.UTF8.GetBytes("the-reply-bytes");

        const string xop = "http://www.w3.org/2004/08/xop/include";

        await using var ctx = new RouteContext();
        ctx.AddComponent(new SoapComponent());
        ctx.AddToRegistry("mtom", new SoapConnectionFactory
        {
            EndpointUrl = $"http://127.0.0.1:{port}/svc",
            Mtom = true,
        });
        ctx.AddRoutes(r =>
        {
            r.From(SoapDsl.Listen("/svc").Host("127.0.0.1").Port(port).ConnectionFactory("mtom"))
                .Process(e =>
                {
                    consumerGot = e.In.GetHeader<IReadOnlyList<SoapAttachment>>(SoapHeaders.Attachments)?[0].Content;
                    e.In.Body = $"<Ack xmlns=\"urn:test\"><data><xop:Include xmlns:xop=\"{xop}\" href=\"cid:resp-1\"/></data></Ack>";
                    e.In.Headers[SoapHeaders.Attachments] =
                        new List<SoapAttachment> { new("resp-1", "text/plain", replied) };
                });
            r.From("direct://call").To(SoapDsl.Call($"http://127.0.0.1:{port}/svc").ConnectionFactory("mtom"));
        });

        await ctx.Start();
        var producer = ctx.GetEndpoint("direct://call").CreateProducer();
        await producer.Start();

        var msg = new Message($"<Upload xmlns=\"urn:test\"><file><xop:Include xmlns:xop=\"{xop}\" href=\"cid:up-1\"/></file></Upload>");
        msg.Headers[SoapHeaders.Attachments] = new List<SoapAttachment> { new("up-1", "application/octet-stream", uploaded) };
        var exchange = new Exchange(msg);
        await producer.Process(exchange);

        // The consumer received our uploaded attachment, and its reply attachment came back to the producer.
        consumerGot.Should().Equal(uploaded);
        var back = exchange.Out!.GetHeader<IReadOnlyList<SoapAttachment>>(SoapHeaders.Attachments);
        back.Should().ContainSingle();
        back![0].Content.Should().Equal(replied);
    }

    [Fact]
    public async Task Mtom_Consumer_DoesNotEcho_InboundAttachments_WhenRouteSetsNoReplyList()
    {
        var port = FreePort();

        await using var ctx = new RouteContext();
        ctx.AddComponent(new SoapComponent());
        ctx.AddToRegistry("mtom", new SoapConnectionFactory
        {
            EndpointUrl = $"http://127.0.0.1:{port}/svc",
            Mtom = true,
        });
        ctx.AddRoutes(r =>
        {
            r.From(SoapDsl.Listen("/svc").Host("127.0.0.1").Port(port).ConnectionFactory("mtom"))
                .Process(e =>
                {
                    // Read the inbound attachment but do NOT put a distinct list on the reply.
                    _ = e.In.GetHeader<IReadOnlyList<SoapAttachment>>(SoapHeaders.Attachments);
                    e.In.Body = "<Ack xmlns=\"urn:test\">ok</Ack>";
                });
            r.From("direct://call").To(SoapDsl.Call($"http://127.0.0.1:{port}/svc").ConnectionFactory("mtom"));
        });

        await ctx.Start();
        var producer = ctx.GetEndpoint("direct://call").CreateProducer();
        await producer.Start();

        var msg = new Message("<Upload xmlns=\"urn:test\"/>");
        msg.Headers[SoapHeaders.Attachments] =
            new List<SoapAttachment> { new("up-1", "application/octet-stream", Encoding.UTF8.GetBytes("caller-secret")) };
        var exchange = new Exchange(msg);
        await producer.Process(exchange);

        // The caller's own inbound attachment must NOT come back on the response.
        var back = exchange.Out!.GetHeader<IReadOnlyList<SoapAttachment>>(SoapHeaders.Attachments);
        (back?.Count ?? 0).Should().Be(0);
    }
}

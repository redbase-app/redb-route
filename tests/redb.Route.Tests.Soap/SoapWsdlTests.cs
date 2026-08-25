using System.Net;
using System.Net.Sockets;
using FluentAssertions;
using redb.Route.Core;
using redb.Route.Soap;
using SoapDsl = redb.Route.Soap.Fluent.Soap;

namespace redb.Route.Tests.Soap;

/// <summary>
/// WSDL publishing (camel-cxf <c>?wsdl</c> parity): a consumer with a configured WSDL serves it on GET, with
/// the <c>soap:address</c> rewritten to the caller's URL; without one, GET is not allowed.
/// </summary>
public class SoapWsdlTests
{
    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private const string SampleWsdl =
        "<definitions xmlns=\"http://schemas.xmlsoap.org/wsdl/\" " +
        "xmlns:soap=\"http://schemas.xmlsoap.org/wsdl/soap/\" targetNamespace=\"urn:test\">" +
          "<portType name=\"EchoPort\"><operation name=\"Echo\"/></portType>" +
          "<service name=\"EchoService\"><port name=\"EchoPort\">" +
            "<soap:address location=\"http://placeholder.invalid/old\"/>" +
          "</port></service>" +
        "</definitions>";

    [Fact]
    public void RewriteAddress_ReplacesSoapLocation()
    {
        var rewritten = SoapWsdl.RewriteAddress(SampleWsdl, "https://real.example/svc");
        rewritten.Should().Contain("https://real.example/svc").And.NotContain("placeholder.invalid");
    }

    [Fact]
    public async Task Consumer_PublishesWsdl_On_Get_WithRewrittenAddress()
    {
        var port = FreePort();

        await using var ctx = new RouteContext();
        ctx.AddComponent(new SoapComponent());
        ctx.AddToRegistry("pub", new SoapConnectionFactory { Wsdl = SampleWsdl });
        ctx.AddRoutes(r => r.From(SoapDsl.Listen("/svc").Host("127.0.0.1").Port(port).ConnectionFactory("pub"))
            .Process(e => e.In.Body = "<Ack xmlns=\"urn:test\"/>"));
        await ctx.Start();

        using var http = new HttpClient();
        var resp = await http.GetAsync($"http://127.0.0.1:{port}/svc?wsdl");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        resp.Content.Headers.ContentType!.MediaType.Should().Be("text/xml");
        var body = await resp.Content.ReadAsStringAsync();
        body.Should().Contain("EchoService").And.Contain("Echo");
        body.Should().Contain($"http://127.0.0.1:{port}/svc").And.NotContain("placeholder.invalid");
    }

    [Fact]
    public async Task Consumer_WithoutWsdl_RejectsGet()
    {
        var port = FreePort();

        await using var ctx = new RouteContext();
        ctx.AddComponent(new SoapComponent());
        ctx.AddRoutes(r => r.From(SoapDsl.Listen("/svc").Host("127.0.0.1").Port(port))
            .Process(e => e.In.Body = "<Ack/>"));
        await ctx.Start();

        using var http = new HttpClient();
        var resp = await http.GetAsync($"http://127.0.0.1:{port}/svc?wsdl");

        // No WSDL configured ⇒ GET is not a registered method on this path.
        resp.StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed);
    }
}

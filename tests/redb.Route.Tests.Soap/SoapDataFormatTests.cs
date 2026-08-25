using System.Net;
using System.Net.Sockets;
using System.Xml.Serialization;
using FluentAssertions;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Soap;
using SoapDsl = redb.Route.Soap.Fluent.Soap;

namespace redb.Route.Tests.Soap;

/// <summary>
/// camel-cxf dataFormat parity: MESSAGE (whole-envelope transparent proxy) and POJO (typed request/response
/// via XmlSerializer). PAYLOAD is covered by the loopback suite.
/// </summary>
public class SoapDataFormatTests
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
    public async Task MessageDataFormat_PassesWholeEnvelopeBothWays()
    {
        var port = FreePort();
        string? routeSawBody = null;

        await using var ctx = new RouteContext();
        ctx.AddComponent(new SoapComponent());
        ctx.AddToRegistry("msg", new SoapConnectionFactory
        {
            EndpointUrl = $"http://127.0.0.1:{port}/svc",
            DataFormat = SoapDataFormat.Message,
        });
        ctx.AddRoutes(r =>
        {
            r.From(SoapDsl.Listen("/svc").Host("127.0.0.1").Port(port).ConnectionFactory("msg"))
                .Process(e =>
                {
                    routeSawBody = e.In.Body!.ToString();
                    // Transparent proxy: the route replies with a whole envelope of its own.
                    e.In.Body =
                        "<soap:Envelope xmlns:soap=\"http://schemas.xmlsoap.org/soap/envelope/\">" +
                          "<soap:Body><Pong xmlns=\"urn:test\">42</Pong></soap:Body></soap:Envelope>";
                });
            r.From("direct://call").To(SoapDsl.Call($"http://127.0.0.1:{port}/svc").ConnectionFactory("msg"));
        });

        await ctx.Start();
        var producer = ctx.GetEndpoint("direct://call").CreateProducer();
        await producer.Start();

        var exchange = new Exchange(new Message(
            "<soap:Envelope xmlns:soap=\"http://schemas.xmlsoap.org/soap/envelope/\">" +
              "<soap:Body><Ping xmlns=\"urn:test\"/></soap:Body></soap:Envelope>"));
        await producer.Process(exchange);

        // The consumer saw the full inbound envelope, and the producer got the full reply envelope.
        routeSawBody.Should().Contain("<soap:Envelope").And.Contain("Ping");
        exchange.Out!.Body!.ToString().Should().Contain("<soap:Envelope").And.Contain("Pong").And.Contain("42");
    }

    [XmlRoot("GetFareRequest", Namespace = "urn:fares")]
    public class GetFareRequest { public string Route { get; set; } = ""; }

    [XmlRoot("GetFareResponse", Namespace = "urn:fares")]
    public class GetFareResponse { public int Price { get; set; } }

    [Fact]
    public async Task PojoDataFormat_TypedRequestAndResponse()
    {
        var port = FreePort();
        string? gotRoute = null;

        await using var ctx = new RouteContext();
        ctx.AddComponent(new SoapComponent());
        ctx.AddToRegistry("client", new SoapConnectionFactory
        {
            EndpointUrl = $"http://127.0.0.1:{port}/svc",
            DataFormat = SoapDataFormat.Pojo,
            ResponseType = typeof(GetFareResponse),
        });
        ctx.AddToRegistry("server", new SoapConnectionFactory
        {
            DataFormat = SoapDataFormat.Pojo,
            RequestType = typeof(GetFareRequest),
        });
        ctx.AddRoutes(r =>
        {
            r.From(SoapDsl.Listen("/svc").Host("127.0.0.1").Port(port).ConnectionFactory("server"))
                .Process(e =>
                {
                    var req = (GetFareRequest)e.In.Body!;
                    gotRoute = req.Route;
                    e.In.Body = new GetFareResponse { Price = 100 };
                });
            r.From("direct://call").To(SoapDsl.Call($"http://127.0.0.1:{port}/svc").ConnectionFactory("client"));
        });

        await ctx.Start();
        var producer = ctx.GetEndpoint("direct://call").CreateProducer();
        await producer.Start();

        var exchange = new Exchange(new Message(new GetFareRequest { Route = "JFK-LHR" }));
        await producer.Process(exchange);

        // The consumer received a typed request object; the producer got back a typed response object.
        gotRoute.Should().Be("JFK-LHR");
        exchange.Out!.Body.Should().BeOfType<GetFareResponse>().Which.Price.Should().Be(100);
    }
}

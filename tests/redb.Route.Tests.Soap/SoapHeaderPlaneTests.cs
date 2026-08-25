using System.Net;
using System.Net.Sockets;
using FluentAssertions;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Soap;
using SoapDsl = redb.Route.Soap.Fluent.Soap;

namespace redb.Route.Tests.Soap;

/// <summary>Ф3: the two header planes — envelope <c>&lt;soap:Header&gt;</c> vs transport — round-trip.</summary>
public class SoapHeaderPlaneTests
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
    public async Task EnvelopeHeader_And_Operation_ReachTheRoute()
    {
        var port = FreePort();
        string? gotHeaderTo = null;
        string? gotOperation = null;

        await using var ctx = new RouteContext();
        ctx.AddComponent(new SoapComponent());
        ctx.AddRoutes(r =>
        {
            r.From(SoapDsl.Listen("/svc").Host("127.0.0.1").Port(port))
                .Process(e =>
                {
                    gotHeaderTo = e.In.GetHeader<string>(SoapHeaders.HeaderPrefix + "To");
                    gotOperation = e.In.GetHeader<string>(SoapHeaders.Operation);
                    e.In.Body = "<Ack xmlns=\"urn:test\">ok</Ack>";
                });
            r.From("direct://call")
                .SetHeader(SoapHeaders.HeaderPrefix + "To", "<To xmlns=\"urn:wsa\">svc</To>")
                .To(SoapDsl.Call($"http://127.0.0.1:{port}/svc"));
        });

        await ctx.Start();
        var producer = ctx.GetEndpoint("direct://call").CreateProducer();
        await producer.Start();

        await producer.Process(new Exchange(new Message("<Ping xmlns=\"urn:test\"><n>1</n></Ping>")));

        gotHeaderTo.Should().NotBeNull().And.Contain("svc");   // envelope header plane crossed the wire
        gotOperation.Should().Be("Ping");                       // operation = body root element
    }
}

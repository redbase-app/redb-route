using System.Net;
using System.Net.Sockets;
using FluentAssertions;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Soap;
using SoapDsl = redb.Route.Soap.Fluent.Soap;

namespace redb.Route.Tests.Soap;

/// <summary>
/// End-to-end loopback: the SOAP producer POSTs a real envelope over a live Kestrel to the SOAP consumer,
/// which delivers the body to a route and returns a response envelope the producer parses. Proves both sides.
/// </summary>
public class SoapLoopbackTests
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
    public async Task Producer_To_Consumer_RoundTrips()
    {
        var port = FreePort();
        var received = new List<string>();

        await using var ctx = new RouteContext();
        ctx.AddComponent(new SoapComponent());
        ctx.AddRoutes(r =>
        {
            r.From(SoapDsl.Listen("/svc").Host("127.0.0.1").Port(port))
                .Process(e =>
                {
                    received.Add(e.In.Body!.ToString()!);
                    e.In.Body = "<Reply xmlns=\"urn:test\"><status>ok</status></Reply>";
                });
            r.From("direct://call")
                .To(SoapDsl.Call($"http://127.0.0.1:{port}/svc").Operation("Ping"));
        });

        await ctx.Start();
        var producer = ctx.GetEndpoint("direct://call").CreateProducer();
        await producer.Start();

        var exchange = new Exchange(new Message("<Ping xmlns=\"urn:test\"><n>1</n></Ping>"));
        await producer.Process(exchange);

        received.Should().ContainSingle().Which.Should().Contain("Ping").And.Contain("1");
        exchange.Out.Should().NotBeNull();
        exchange.Out!.Body!.ToString().Should().Contain("Reply").And.Contain("ok");
    }

    [Fact]
    public async Task Soap12_RoundTrips_EndToEnd()
    {
        var port = FreePort();
        string? received = null;

        await using var ctx = new RouteContext();
        ctx.AddComponent(new SoapComponent());
        ctx.AddToRegistry("v12", new SoapConnectionFactory
        {
            EndpointUrl = $"http://127.0.0.1:{port}/svc12",
            SoapVersion = SoapVersion.Soap12,
        });
        ctx.AddRoutes(r =>
        {
            r.From(SoapDsl.Listen("/svc12").Host("127.0.0.1").Port(port))
                .Process(e => { received = e.In.Body!.ToString(); e.In.Body = "<Reply xmlns=\"urn:test\">ok</Reply>"; });
            r.From("direct://call12")
                .To(SoapDsl.Call($"http://127.0.0.1:{port}/svc12").ConnectionFactory("v12").Operation("Ping"));
        });

        await ctx.Start();
        var producer = ctx.GetEndpoint("direct://call12").CreateProducer();
        await producer.Start();

        var exchange = new Exchange(new Message("<Ping xmlns=\"urn:test\"><n>2</n></Ping>"));
        await producer.Process(exchange);

        received.Should().Contain("Ping").And.Contain("2");
        exchange.Out!.Body!.ToString().Should().Contain("Reply").And.Contain("ok");
    }

    [Fact]
    public async Task Consumer_Honours_The_Fault_Code_A_Route_Chose()
    {
        // SoapFaultException carries a FaultCode and SoapEnvelope.BuildFault accepts one, but the consumer
        // used to drop it and answer soap:Server for everything. A route could describe what went wrong and
        // not what kind of wrong it was.
        //
        // That matters wherever a client branches on the code rather than on prose: WS-Trust clients treat
        // wst:FailedAuthentication and wst:InvalidRequest as different outcomes, and prose is not a contract.
        var port = FreePort();

        await using var ctx = new RouteContext();
        ctx.AddComponent(new SoapComponent());
        ctx.AddRoutes(r =>
        {
            r.From(SoapDsl.Listen("/svc-code").Host("127.0.0.1").Port(port))
                .Process(_ => throw new SoapFaultException("wst:FailedAuthentication", "bad credentials"));
            r.From("direct://call-code")
                .To(SoapDsl.Call($"http://127.0.0.1:{port}/svc-code"));
        });

        await ctx.Start();
        var producer = ctx.GetEndpoint("direct://call-code").CreateProducer();
        await producer.Start();

        var act = async () => await producer.Process(new Exchange(new Message("<Ping/>")));

        var ex = await act.Should().ThrowAsync<SoapFaultException>();
        ex.Which.FaultCode.Should().Contain("FailedAuthentication",
            "the code the route chose has to survive to the caller, not be flattened to soap:Server");
        ex.Which.FaultString.Should().Be("bad credentials");
    }

    [Fact]
    public async Task Consumer_RouteException_Returns_SoapFault()
    {
        var port = FreePort();

        await using var ctx = new RouteContext();
        ctx.AddComponent(new SoapComponent());
        ctx.AddRoutes(r =>
        {
            r.From(SoapDsl.Listen("/svc-err").Host("127.0.0.1").Port(port))
                .Process(_ => throw new InvalidOperationException("route blew up"));
            r.From("direct://call-err")
                .To(SoapDsl.Call($"http://127.0.0.1:{port}/svc-err"));
        });

        await ctx.Start();
        var producer = ctx.GetEndpoint("direct://call-err").CreateProducer();
        await producer.Start();

        var exchange = new Exchange(new Message("<Ping xmlns=\"urn:test\"/>"));
        var act = async () => await producer.Process(exchange);

        // A route exception on the server side comes back as a SOAP fault the producer surfaces — but
        // BR-4: the exception's own text is not what travels. An unhandled failure yields a generic
        // faultstring with a reference; a route that wants to say more raises SoapFaultException, whose
        // FaultString is deliberate and is still sent verbatim (covered by the fault-code tests).
        await act.Should().ThrowAsync<SoapFaultException>()
            .Where(e => !e.FaultString!.Contains("route blew up") && e.FaultString!.Contains("ref: "));
    }

    [Fact]
    public async Task StringUri_Forms_Drive_The_Call_EndToEnd()
    {
        var port = FreePort();
        string? received = null;

        await using var ctx = new RouteContext();
        ctx.AddComponent(new SoapComponent());
        ctx.AddRoutes(r =>
        {
            // Both endpoints written as plain string URIs (the forms documented in README.md), no fluent builder.
            // A MULTI-segment path guards against first-segment truncation (the URI-authority parsing bug the
            // single-slash `soap:/path` form fixes, mirroring the AS2/Http listeners).
            r.From($"soap:/api/orders?host=127.0.0.1&port={port}")
                .Process(e => { received = e.In.Body!.ToString(); e.In.Body = "<Reply xmlns=\"urn:test\">ok</Reply>"; });
            r.From("direct://call")
                .To($"soap://127.0.0.1:{port}/api/orders?operation=Ping");
        });

        await ctx.Start();
        var producer = ctx.GetEndpoint("direct://call").CreateProducer();
        await producer.Start();

        var exchange = new Exchange(new Message("<Ping xmlns=\"urn:test\"><n>9</n></Ping>"));
        await producer.Process(exchange);

        received.Should().Contain("Ping").And.Contain("9");
        exchange.Out!.Body!.ToString().Should().Contain("Reply").And.Contain("ok");
    }

    [Fact]
    public async Task Consumer_ByteArray_Reply_Is_Sent_As_Xml_Not_Stringified()
    {
        var port = FreePort();

        await using var ctx = new RouteContext();
        ctx.AddComponent(new SoapComponent());
        ctx.AddRoutes(r =>
        {
            r.From(SoapDsl.Listen("/svc").Host("127.0.0.1").Port(port))
                // A route that produces already-serialized XML bytes (Payload mode).
                .Process(e => e.In.Body = System.Text.Encoding.UTF8.GetBytes("<Reply xmlns=\"urn:test\">ok</Reply>"));
            r.From("direct://call").To(SoapDsl.Call($"http://127.0.0.1:{port}/svc"));
        });

        await ctx.Start();
        var producer = ctx.GetEndpoint("direct://call").CreateProducer();
        await producer.Start();

        var exchange = new Exchange(new Message("<Ping xmlns=\"urn:test\"/>"));
        await producer.Process(exchange);

        var body = exchange.Out!.Body!.ToString()!;
        body.Should().Contain("Reply").And.Contain("ok");
        body.Should().NotContain("System.Byte");
    }

    [Fact]
    public void Fluent_Builders_Emit_The_Documented_String_Uris()
    {
        // Locks the README string examples to the actual DSL output — a guard against fluent↔URI drift.
        ((string)SoapDsl.Call("https://gds/air.svc").ConnectionFactory("amadeus").Operation("GetFares"))
            .Should().Be("soaps://gds/air.svc?connectionFactory=amadeus&operation=GetFares");
        ((string)SoapDsl.Listen("/svc/orders").Host("0.0.0.0").Port(4090).ConnectionFactory("orders"))
            .Should().Be("soap:/svc/orders?host=0.0.0.0&port=4090&connectionFactory=orders");
    }
}

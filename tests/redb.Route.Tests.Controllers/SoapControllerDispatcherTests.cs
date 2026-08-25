using System.Net;
using System.Net.Sockets;
using System.Xml.Serialization;
using FluentAssertions;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Controllers;
using redb.Route.Controllers.Attributes;
using redb.Route.Controllers.Extensions;
using redb.Route.Soap;
using SoapDsl = redb.Route.Soap.Fluent.Soap;

namespace redb.Route.Tests.Controllers;

/// <summary>
/// SOAP as a controller transport: <see cref="SoapControllerDispatcher"/> maps <c>redbSoap.operation</c> to a
/// method, binds the XML body, and serializes the result back — unit tests plus a full end-to-end run through
/// the real SOAP consumer.
/// </summary>
public class SoapControllerDispatcherTests
{
    [XmlRoot("GetFares", Namespace = "urn:air")]
    public class GetFares { public string Route { get; set; } = ""; }

    [XmlRoot("GetFaresResponse", Namespace = "urn:air")]
    public class GetFaresResponse { public int Price { get; set; } }

    public class AirController : RedbController
    {
        public Task<GetFaresResponse> GetFares([FromBody] GetFares req)
            => Task.FromResult(new GetFaresResponse { Price = req.Route == "JFK-LHR" ? 100 : 0 });

        [SoapOperation("Ping")]
        public string Health() => "pong";

        // Returns already-serialized XML as bytes: must reach the client as XML, not "System.Byte[]".
        public byte[] Raw() => System.Text.Encoding.UTF8.GetBytes("<Raw xmlns=\"urn:air\">ok</Raw>");

        // A required simple parameter with no binding attribute and no default: must fault readably.
        public string Bad(int n) => n.ToString();

        // ValueTask<T> must be awaited and its result serialized (not the struct itself).
        public ValueTask<GetFaresResponse> Quote([FromBody] GetFares req)
            => ValueTask.FromResult(new GetFaresResponse { Price = 42 });
    }

    // A second controller that reuses the "GetFares" operation name — an unresolvable ambiguity.
    public class ClashController : RedbController
    {
        public string GetFares() => "clash";
    }

    private static IExchange Exchange(string? operation, string? bodyXml)
    {
        var ex = new Exchange();
        if (operation is not null) ex.In.setHeader(SoapControllerDispatcher.OperationHeader, operation);
        if (bodyXml is not null) ex.In.Body = bodyXml;
        return ex;
    }

    [Fact]
    public async Task Dispatches_By_Operation_And_Binds_Xml_Body()
    {
        var dispatcher = new SoapControllerDispatcher(new RouteContext(), typeof(AirController));
        var ex = Exchange("GetFares", "<GetFares xmlns=\"urn:air\"><Route>JFK-LHR</Route></GetFares>");

        await dispatcher.Process(ex);

        ex.Out.Should().NotBeNull();
        ex.Out!.Body!.ToString().Should().Contain("GetFaresResponse").And.Contain("100");
    }

    [Fact]
    public async Task SoapOperation_Attribute_Maps_The_Method()
    {
        var dispatcher = new SoapControllerDispatcher(new RouteContext(), typeof(AirController));
        var ex = Exchange("Ping", null);   // method is Health(), mapped via [SoapOperation("Ping")]

        await dispatcher.Process(ex);

        ex.Out!.Body!.ToString().Should().Be("pong");
    }

    [Fact]
    public async Task Unknown_Operation_Throws()
    {
        var dispatcher = new SoapControllerDispatcher(new RouteContext(), typeof(AirController));
        var act = async () => await dispatcher.Process(Exchange("DoesNotExist", null));
        await act.Should().ThrowAsync<InvalidOperationException>().Where(e => e.Message.Contains("DoesNotExist"));
    }

    [Fact]
    public async Task Missing_Operation_Header_Throws()
    {
        var dispatcher = new SoapControllerDispatcher(new RouteContext(), typeof(AirController));
        var act = async () => await dispatcher.Process(Exchange(null, null));
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Unbindable_Simple_Param_Throws_Readable_Error()
    {
        var dispatcher = new SoapControllerDispatcher(new RouteContext(), typeof(AirController));
        var act = async () => await dispatcher.Process(Exchange("Bad", "<Bad xmlns=\"urn:air\"/>"));
        // A clear message naming the parameter, not a cryptic reflection ArgumentException.
        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("'n'");
    }

    [Fact]
    public void Ctor_Throws_On_Ambiguous_Operation_Across_Controllers()
    {
        var act = () => new SoapControllerDispatcher(new RouteContext(), typeof(AirController), typeof(ClashController));
        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("Ambiguous").And.Contain("GetFares");
    }

    [Fact]
    public async Task Dispatches_ValueTask_Return()
    {
        var dispatcher = new SoapControllerDispatcher(new RouteContext(), typeof(AirController));
        var ex = Exchange("Quote", "<GetFares xmlns=\"urn:air\"><Route>X</Route></GetFares>");
        await dispatcher.Process(ex);
        // The ValueTask<T> must be awaited and its result serialized, not the struct itself.
        ex.Out!.Body!.ToString().Should().Contain("GetFaresResponse").And.Contain("42");
    }

    [Fact]
    public async Task Controller_ByteArray_Reply_Is_Decoded_Not_Stringified()
    {
        var port = FreePort();

        await using var ctx = new RouteContext();
        ctx.AddComponent(new SoapComponent());
        ctx.AddRoutes(r =>
        {
            r.From(SoapDsl.Listen("/air").Host("127.0.0.1").Port(port))
                .RedbSoapController<AirController>();
            r.From("direct://call")
                .To(SoapDsl.Call($"http://127.0.0.1:{port}/air"));
        });

        await ctx.Start();
        var producer = ctx.GetEndpoint("direct://call").CreateProducer();
        await producer.Start();

        var exchange = new Exchange(new Message("<Raw xmlns=\"urn:air\"/>"));
        await producer.Process(exchange);

        var body = exchange.Out!.Body!.ToString()!;
        body.Should().Contain("Raw").And.Contain("ok");
        body.Should().NotContain("System.Byte");
    }

    [Fact]
    public async Task EndToEnd_Through_The_Real_Soap_Consumer()
    {
        var port = FreePort();

        await using var ctx = new RouteContext();
        ctx.AddComponent(new SoapComponent());
        ctx.AddRoutes(r =>
        {
            // A SOAP endpoint served by a controller — dispatch by operation, no fluent route logic.
            r.From(SoapDsl.Listen("/air").Host("127.0.0.1").Port(port))
                .RedbSoapController<AirController>();
            r.From("direct://call")
                .To(SoapDsl.Call($"http://127.0.0.1:{port}/air"));
        });

        await ctx.Start();
        var producer = ctx.GetEndpoint("direct://call").CreateProducer();
        await producer.Start();

        var exchange = new Exchange(new Message("<GetFares xmlns=\"urn:air\"><Route>JFK-LHR</Route></GetFares>"));
        await producer.Process(exchange);

        // The controller ran behind the SOAP consumer and its typed reply came back as the response body.
        exchange.Out!.Body!.ToString().Should().Contain("GetFaresResponse").And.Contain("100");
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }
}

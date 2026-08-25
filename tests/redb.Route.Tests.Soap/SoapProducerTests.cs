using System.Net;
using System.Net.Sockets;
using System.Text;
using FluentAssertions;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Soap;
using SoapDsl = redb.Route.Soap.Fluent.Soap;

namespace redb.Route.Tests.Soap;

/// <summary>Producer tests against a loopback HTTP server that speaks SOAP by hand (in-box, no WCF).</summary>
public class SoapProducerTests
{
    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private sealed class SoapStub : IDisposable
    {
        private readonly HttpListener _listener = new();
        public byte[]? LastRequest { get; private set; }
        public string Url { get; }

        public SoapStub(Func<byte[], byte[]> respond)
        {
            var port = FreePort();
            Url = $"http://localhost:{port}/svc";
            _listener.Prefixes.Add($"http://localhost:{port}/");
            _listener.Start();
            _ = Task.Run(async () =>
            {
                while (_listener.IsListening)
                {
                    HttpListenerContext ctx;
                    try { ctx = await _listener.GetContextAsync(); }
                    catch { break; }
                    using var ms = new MemoryStream();
                    await ctx.Request.InputStream.CopyToAsync(ms);
                    LastRequest = ms.ToArray();
                    var resp = respond(LastRequest);
                    ctx.Response.ContentType = "text/xml; charset=utf-8";
                    ctx.Response.OutputStream.Write(resp);
                    ctx.Response.Close();
                }
            });
        }

        public void Dispose() { try { _listener.Stop(); } catch { } _listener.Close(); }
    }

    private static async Task<IExchange> CallAsync(string url, IExchange exchange, Action<SoapConnectionFactory>? tune = null)
    {
        await using var ctx = new RouteContext();
        ctx.AddComponent(new SoapComponent());
        var factory = new SoapConnectionFactory { EndpointUrl = url, SoapVersion = SoapVersion.Soap11 };
        tune?.Invoke(factory);
        ctx.AddToRegistry("svc", factory);
        ctx.AddRoutes(r => r.From("direct://call")
            .To(SoapDsl.Call(url).ConnectionFactory("svc").Operation("GetFares")));

        await ctx.Start();
        var producer = ctx.GetEndpoint("direct://call").CreateProducer();
        await producer.Start();
        await producer.Process(exchange);
        return exchange;
    }

    [Fact]
    public async Task Producer_SendsEnvelope_And_ReturnsResponseBody()
    {
        const string response = "<GetFaresResponse xmlns=\"urn:test\"><fare>100</fare></GetFaresResponse>";
        using var stub = new SoapStub(_ => SoapEnvelope.Build(response, SoapVersion.Soap11));

        var exchange = new Exchange(new Message("<GetFares xmlns=\"urn:test\"><from>SVO</from></GetFares>"));
        await CallAsync(stub.Url, exchange);

        // The server received a valid SOAP envelope carrying our payload.
        var received = Encoding.UTF8.GetString(stub.LastRequest!);
        received.Should().Contain("Envelope").And.Contain("GetFares").And.Contain("SVO");

        // The response body came back on Out.
        exchange.Out.Should().NotBeNull();
        exchange.Out!.Body!.ToString().Should().Contain("GetFaresResponse").And.Contain("100");
    }

    [Fact]
    public async Task Producer_OnFault_Throws_And_SetsFaultHeaders()
    {
        using var stub = new SoapStub(_ => SoapEnvelope.BuildFault("no such route", SoapVersion.Soap11, "soap:Client"));

        var exchange = new Exchange(new Message("<GetFares xmlns=\"urn:test\"/>"));
        var act = async () => await CallAsync(stub.Url, exchange);

        await act.Should().ThrowAsync<SoapFaultException>().Where(e => e.FaultString == "no such route");
        exchange.In.GetHeader<string>(SoapHeaders.FaultCode).Should().Be("soap:Client");
    }
}

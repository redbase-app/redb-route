using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using FluentAssertions;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Soap;
using SoapDsl = redb.Route.Soap.Fluent.Soap;

namespace redb.Route.Tests.Soap;

/// <summary>
/// A fault the route <b>means</b> to send is a reply, not a failure. Until now the only way to answer
/// with <c>soap:Fault</c> was to throw, which left the exchange failed: the route's metrics counted an
/// error, a dead-letter channel took a copy, and a supervising host saw a broken route — for a "no such
/// order" the service is designed to return. Reported by the Tsak agent 2026-09-25, whose DLQ filled
/// with business faults.
/// <para>
/// The answer is the one the HTTP consumer already uses for a status code: the route puts it in the
/// reply's headers and returns normally. Throwing still works and still means a failure — an exception
/// the route did not intend is exactly that.
/// </para>
/// </summary>
public class SoapFaultAsReplyTests
{
    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private sealed class OutcomeListener : IRouteLifecycleListener
    {
        public int Failed;
        public int Completed;

        public Task OnExchangeCompleted(string routeId, IExchange exchange, CancellationToken ct)
        {
            Interlocked.Increment(ref Completed);
            return Task.CompletedTask;
        }

        public Task OnExchangeFailed(string routeId, IExchange exchange, Exception exception, CancellationToken ct)
        {
            Interlocked.Increment(ref Failed);
            return Task.CompletedTask;
        }
    }

    private static async Task<(HttpStatusCode Status, string? Code, string? Reason)> Post(
        int port, SoapVersion version, string bodyXml = "<GetOrder xmlns=\"urn:t\"/>")
    {
        using var http = new HttpClient();
        var content = new ByteArrayContent(SoapEnvelope.Build(bodyXml, version));
        content.Headers.ContentType = MediaTypeHeaderValue.Parse(SoapEnvelope.ContentType(version, "urn:t/GetOrder"));

        using var resp = await http.PostAsync($"http://127.0.0.1:{port}/svc", content);
        var parsed = SoapEnvelope.Parse(await resp.Content.ReadAsByteArrayAsync(), version);
        return (resp.StatusCode, parsed.IsFault ? parsed.FaultCode : null, parsed.IsFault ? parsed.FaultString : null);
    }

    [Theory]
    [InlineData(SoapVersion.Soap11, "soap:Client")]
    [InlineData(SoapVersion.Soap12, "soap:Sender")]
    public async Task Fault_headers_on_the_reply_send_a_fault_and_the_exchange_succeeds(SoapVersion version, string expectedCode)
    {
        var port = FreePort();
        var outcome = new OutcomeListener();
        await using var ctx = new RouteContext();
        ctx.AddComponent(new SoapComponent());
        ctx.AddLifecycleListener(outcome);
        ctx.AddRoutes(r => r.From(SoapDsl.Listen("/svc").Host("127.0.0.1").Port(port))
            .Process(e => e.In.Headers[SoapHeaders.FaultString] = "No order with that number"));
        await ctx.Start();

        var (status, code, reason) = await Post(port, version);

        reason.Should().Be("No order with that number");
        code.Should().Be(expectedCode, "a refusal about the CONTENT of the request is the sender's, not a retry hint");
        status.Should().Be(version == SoapVersion.Soap12 ? HttpStatusCode.OK : HttpStatusCode.InternalServerError);
        outcome.Failed.Should().Be(0, "the route answered as designed");
        outcome.Completed.Should().Be(1);
    }

    [Fact]
    public async Task The_route_picks_the_fault_code_when_it_wants_another_one()
    {
        var port = FreePort();
        await using var ctx = new RouteContext();
        ctx.AddComponent(new SoapComponent());
        ctx.AddRoutes(r => r.From(SoapDsl.Listen("/svc").Host("127.0.0.1").Port(port))
            .Process(e =>
            {
                e.In.Headers[SoapHeaders.FaultCode] = "wst:FailedAuthentication";
                e.In.Headers[SoapHeaders.FaultString] = "Token rejected";
            }));
        await ctx.Start();

        var (_, code, reason) = await Post(port, SoapVersion.Soap12);

        // WS-Trust clients branch on the code, so a route that names one must have it sent verbatim.
        code.Should().Be("wst:FailedAuthentication");
        reason.Should().Be("Token rejected");
    }

    [Fact]
    public async Task A_thrown_fault_is_still_a_failed_exchange()
    {
        var port = FreePort();
        var outcome = new OutcomeListener();
        await using var ctx = new RouteContext();
        ctx.AddComponent(new SoapComponent());
        ctx.AddLifecycleListener(outcome);
        ctx.AddRoutes(r => r.From(SoapDsl.Listen("/svc").Host("127.0.0.1").Port(port))
            .Process(_ => throw new SoapFaultException("soap:Receiver", "Downstream is down")));
        await ctx.Start();

        var (_, code, reason) = await Post(port, SoapVersion.Soap12);

        reason.Should().Be("Downstream is down");
        code.Should().Be("soap:Receiver");
        outcome.Failed.Should().Be(1, "an exception the route did not intend stays a failure");
    }

    [Fact]
    public async Task A_reply_without_fault_headers_is_an_ordinary_reply()
    {
        var port = FreePort();
        await using var ctx = new RouteContext();
        ctx.AddComponent(new SoapComponent());
        ctx.AddRoutes(r => r.From(SoapDsl.Listen("/svc").Host("127.0.0.1").Port(port))
            .Process(e => e.In.Body = "<Ack xmlns=\"urn:t\">ok</Ack>"));
        await ctx.Start();

        var (status, code, _) = await Post(port, SoapVersion.Soap12);

        code.Should().BeNull();
        status.Should().Be(HttpStatusCode.OK);
    }
}

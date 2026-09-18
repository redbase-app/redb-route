using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Claims;
using FluentAssertions;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Http;
using redb.Route.Soap;
using SoapDsl = redb.Route.Soap.Fluent.Soap;

namespace redb.Route.Tests.Soap;

/// <summary>
/// The caller's identity on SOAP exchanges: the shared host's resolver identifies the HTTP request that
/// carries the envelope, and the consumer puts the principal on the exchange (<see cref="ExchangePrincipal"/>).
/// </summary>
public sealed class SoapPrincipalTests
{
    [Theory]
    [InlineData("good", "soap-user")]
    [InlineData(null, "anonymous")]
    public async Task The_resolved_caller_reaches_the_route(string? token, string expected)
    {
        var port = FreePort();
        await using var manager = new SharedHttpServerManager(new HttpHostingOptions
        {
            ResolvePrincipal = http => Task.FromResult(http.Request.Headers["X-Test-Token"].ToString() == "good"
                ? new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "soap-user")], "test"))
                : null),
        });

        await using var context = new RouteContext();
        context.AddComponent(new SoapComponent { ServerManager = manager });
        context.AddRoutes(r => r.From(SoapDsl.Listen("/svc").Host("127.0.0.1").Port(port))
            .Process(e => e.In.Body =
                $"<Who xmlns=\"urn:t\">{ExchangePrincipal.Get(e)?.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "anonymous"}</Who>"));
        await context.Start();

        using var client = new HttpClient();
        var content = new ByteArrayContent(SoapEnvelope.Build("<Ask xmlns=\"urn:t\"/>", SoapVersion.Soap11));
        content.Headers.ContentType = MediaTypeHeaderValue.Parse(SoapEnvelope.ContentType(SoapVersion.Soap11, "urn:t/Ask"));
        using var request = new HttpRequestMessage(System.Net.Http.HttpMethod.Post, $"http://127.0.0.1:{port}/svc") { Content = content };
        if (token is not null) request.Headers.Add("X-Test-Token", token);

        using var response = await client.SendAsync(request);
        var parsed = SoapEnvelope.Parse(await response.Content.ReadAsByteArrayAsync(), SoapVersion.Soap11);

        parsed.IsFault.Should().BeFalse();
        parsed.BodyXml.Should().Contain($">{expected}<");
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

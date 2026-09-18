using System.Net;
using System.Net.Sockets;
using System.Security.Claims;
using redb.Route.Http;
using redb.Route.Tests.Llm.TestHelpers;
using LlmDsl = redb.Route.Llm.Fluent.Llm;

namespace redb.Route.Tests.Llm.Transport;

/// <summary>
/// The joint level: a caller identified by the HTTP transport
/// (<see cref="HttpHostingOptions.ResolvePrincipal"/>) drives an LLM route whose tool demands a claim.
/// <para>
/// The other levels prove the halves — the transport puts the principal on the exchange, the engine
/// enforces claims read from the exchange's principal — and each of them stubs the other side. Here
/// nothing is stubbed: the request goes over a real socket, the resolver identifies it, and the tool
/// fires (or not) on what the transport established.
/// </para>
/// </summary>
[Trait("Category", "Governance")]
public sealed class HttpPrincipalToAgentTests
{
    private const string Schema = """{"type":"object","properties":{"q":{"type":"string"}},"required":["q"]}""";
    private const string Input = """{"q":"x"}""";
    private const string Claim = "orders:read";
    private const string TokenHeader = "X-Test-Token";

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>A resolver that knows one token, in the shape a real middleware would use.</summary>
    private static HttpHostingOptions Hosting(string claim, bool authenticated = true) => new()
    {
        ResolvePrincipal = ctx => Task.FromResult<ClaimsPrincipal?>(
            ctx.Request.Headers[TokenHeader].ToString() == "good"
                ? new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim("scope", claim)], authenticated ? "test" : null))
                : null)
    };

    private static async Task<HttpResponseMessage> SendAsync(int port, bool withToken)
    {
        using var http = new HttpClient();
        using var request = new HttpRequestMessage(System.Net.Http.HttpMethod.Post, $"http://127.0.0.1:{port}/agent")
        {
            Content = new StringContent("Call the tool.")
        };
        if (withToken) request.Headers.Add(TokenHeader, "good");
        return await http.SendAsync(request);
    }

    [Fact]
    public async Task CallerIdentifiedByTheTransport_SatisfiesTheToolClaim()
    {
        var port = FreePort();
        var toolName = $"http_{Guid.NewGuid():N}"[..20];
        var provider = new FakeProvider()
            .EnqueueToolUse(toolName, Input, "tu_1")
            .EnqueueText("done");
        var tool = new EchoToolRoute(toolName, "Look up a fact.", Schema, """{"answer":"42"}""",
            requiredClaims: [Claim]);

        await using var host = LiveLlmHost.Build(
                engineFromDi: true, httpHosting: Hosting(Claim), httpPort: port)
            .AddFactory("demo", new LlmConnectionFactory { Provider = "stub", PrebuiltProvider = provider });

        await host.StartAsync(tool, r => r.From($"http:127.0.0.1:{port}/agent?inOut=true")
            .To(LlmDsl.Factory("demo").Tools(toolName).MaxIterations(4).AsUri())
            .To("mock:done"));

        var response = await SendAsync(port, withToken: true);

        response.IsSuccessStatusCode.Should().BeTrue(
            $"the route ran to completion, got {(int)response.StatusCode}");
        tool.CapturedInputs.Should().HaveCount(1,
            "the claim came from the transport-verified caller — no test hook put anything on the exchange");
    }

    [Fact]
    public async Task CallerWithoutTheClaim_IsDeniedByTheEngine()
    {
        var port = FreePort();
        var toolName = $"httpdeny_{Guid.NewGuid():N}"[..20];
        var provider = new FakeProvider()
            .EnqueueToolUse(toolName, Input, "tu_1")
            .EnqueueText("done");
        var tool = new EchoToolRoute(toolName, "Look up a fact.", Schema, """{"answer":"42"}""",
            requiredClaims: [Claim]);

        // The caller authenticates and holds a different scope: the transport did its job, and the engine
        // still must refuse — an identified caller is not automatically an authorised one.
        await using var host = LiveLlmHost.Build(
                engineFromDi: true, httpHosting: Hosting("billing:read"), httpPort: port)
            .AddFactory("demo", new LlmConnectionFactory { Provider = "stub", PrebuiltProvider = provider });

        await host.StartAsync(tool, r => r.From($"http:127.0.0.1:{port}/agent?inOut=true")
            .To(LlmDsl.Factory("demo").Tools(toolName).MaxIterations(4).AsUri())
            .To("mock:done"));

        await SendAsync(port, withToken: true);

        tool.CapturedInputs.Should().BeEmpty("the tool's claim is not in the caller's scope");
    }

    [Fact]
    public async Task AnonymousCaller_IsDeniedByTheEngine()
    {
        var port = FreePort();
        var toolName = $"httpanon_{Guid.NewGuid():N}"[..20];
        var provider = new FakeProvider()
            .EnqueueToolUse(toolName, Input, "tu_1")
            .EnqueueText("done");
        var tool = new EchoToolRoute(toolName, "Look up a fact.", Schema, """{"answer":"42"}""",
            requiredClaims: [Claim]);

        await using var host = LiveLlmHost.Build(
                engineFromDi: true, httpHosting: Hosting(Claim), httpPort: port)
            .AddFactory("demo", new LlmConnectionFactory { Provider = "stub", PrebuiltProvider = provider });

        await host.StartAsync(tool, r => r.From($"http:127.0.0.1:{port}/agent?inOut=true")
            .To(LlmDsl.Factory("demo").Tools(toolName).MaxIterations(4).AsUri())
            .To("mock:done"));

        await SendAsync(port, withToken: false);

        tool.CapturedInputs.Should().BeEmpty(
            "the resolver refused to identify the caller, so there is no scope to check");
    }

    [Fact]
    public async Task IdentityWithoutAuthenticationType_IsDenied()
    {
        var port = FreePort();
        var toolName = $"httpauth_{Guid.NewGuid():N}"[..20];
        var provider = new FakeProvider()
            .EnqueueToolUse(toolName, Input, "tu_1")
            .EnqueueText("done");
        var tool = new EchoToolRoute(toolName, "Look up a fact.", Schema, """{"answer":"42"}""",
            requiredClaims: [Claim]);

        // The scope claim is present, but the identity was built without an authentication type: the source
        // treats it as unverified, and the tool must not run on an unverified identity.
        await using var host = LiveLlmHost.Build(
                engineFromDi: true, httpHosting: Hosting(Claim, authenticated: false), httpPort: port)
            .AddFactory("demo", new LlmConnectionFactory { Provider = "stub", PrebuiltProvider = provider });

        await host.StartAsync(tool, r => r.From($"http:127.0.0.1:{port}/agent?inOut=true")
            .To(LlmDsl.Factory("demo").Tools(toolName).MaxIterations(4).AsUri())
            .To("mock:done"));

        await SendAsync(port, withToken: true);

        tool.CapturedInputs.Should().BeEmpty(
            "an unauthenticated identity is not a verified caller, whatever claims it carries");
    }
}
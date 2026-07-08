using System.Net;
using Microsoft.AspNetCore.Http;
using redb.Route.Http;

namespace redb.Route.Tests.Http;

/// <summary>
/// Tests for the per-route CORS dispatch middleware in <see cref="SharedHttpServerManager"/>.
/// Verifies the contract introduced in C15 (Sprint C):
/// <list type="bullet">
///   <item>resolver delegate wins over the static whitelist;</item>
///   <item>multi-origin CSV is matched against the request <c>Origin</c>, never echoed verbatim;</item>
///   <item><c>Vary: Origin</c> is always emitted whenever CORS headers are present;</item>
///   <item>preflight (OPTIONS) reflects the request's <c>Access-Control-Request-Headers</c>/<c>-Method</c>;</item>
///   <item>wildcard <c>*</c> combined with credentials is demoted to "no headers" at request time;</item>
///   <item>two routes on the same <c>(host, port)</c> with different policies do not collide.</item>
/// </list>
/// </summary>
[Collection("HttpServer")]
public class CorsDispatchTests : IAsyncLifetime
{
    private SharedHttpServerManager _serverManager = null!;
    private HttpClient _client = null!;
    private int _port;
    private readonly List<RouteRegistration> _registrations = [];

    public Task InitializeAsync()
    {
        _port = GetFreePort();
        _serverManager = new SharedHttpServerManager();
        _client = new HttpClient();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        foreach (var reg in _registrations)
            _serverManager.UnregisterRoute(reg);
        await _serverManager.DisposeAsync();
    }

    private async Task<RouteRegistration> RegisterAsync(string path, RouteCorsOptions cors, string? methods = null)
    {
        var reg = _serverManager.RegisterRoute(
            "127.0.0.1", _port, path, methods,
            ctx => { ctx.Response.StatusCode = 200; return Task.CompletedTask; },
            corsOptions: cors);
        _registrations.Add(reg);
        await _serverManager.EnsureStarted("127.0.0.1", _port);
        return reg;
    }

    // ── Static whitelist ──

    [Fact]
    public async Task SingleOrigin_Echoes_Origin_AndAddsVary()
    {
        await RegisterAsync("/webhook", new RouteCorsOptions(
            AllowedOrigins: "https://example.com",
            AllowedMethods: null,
            AllowCredentials: false));

        var req = new HttpRequestMessage(System.Net.Http.HttpMethod.Get, $"http://127.0.0.1:{_port}/webhook");
        req.Headers.Add("Origin", "https://example.com");
        var res = await _client.SendAsync(req);

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        res.Headers.GetValues("Access-Control-Allow-Origin").Single().Should().Be("https://example.com");
        res.Headers.GetValues("Vary").Should().Contain(v => v.Contains("Origin"));
    }

    [Fact]
    public async Task SingleOrigin_OriginNotAllowed_NoCorsHeaders()
    {
        await RegisterAsync("/webhook", new RouteCorsOptions(
            AllowedOrigins: "https://example.com",
            AllowedMethods: null,
            AllowCredentials: false));

        var req = new HttpRequestMessage(System.Net.Http.HttpMethod.Get, $"http://127.0.0.1:{_port}/webhook");
        req.Headers.Add("Origin", "https://evil.com");
        var res = await _client.SendAsync(req);

        // Request still succeeds (the server must not 4xx unallowed origins on simple requests --
        // it's the browser's job to reject). But no CORS headers are emitted, so the browser will
        // refuse to expose the response to the calling page.
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        res.Headers.Contains("Access-Control-Allow-Origin").Should().BeFalse();
    }

    [Fact]
    public async Task MultiOrigin_Csv_MatchesAndEchoesSingle()
    {
        await RegisterAsync("/webhook", new RouteCorsOptions(
            AllowedOrigins: "https://a.com, https://b.com, https://c.com",
            AllowedMethods: null,
            AllowCredentials: false));

        var req = new HttpRequestMessage(System.Net.Http.HttpMethod.Get, $"http://127.0.0.1:{_port}/webhook");
        req.Headers.Add("Origin", "https://b.com");
        var res = await _client.SendAsync(req);

        // Browsers cannot consume a CSV in Access-Control-Allow-Origin -- the dispatch must
        // single-select and echo back exactly the matching origin.
        res.Headers.GetValues("Access-Control-Allow-Origin").Single().Should().Be("https://b.com");
        res.Headers.GetValues("Vary").Should().Contain(v => v.Contains("Origin"));
    }

    // ── Resolver delegate ──

    [Fact]
    public async Task Resolver_TakesPrecedence_OverStaticWhitelist()
    {
        Func<HttpRequest, string?> resolver = _ => "https://from-resolver.com";

        await RegisterAsync("/webhook", new RouteCorsOptions(
            AllowedOrigins: "https://from-static.com",
            AllowedMethods: null,
            AllowCredentials: false,
            OriginsResolver: resolver));

        var req = new HttpRequestMessage(System.Net.Http.HttpMethod.Get, $"http://127.0.0.1:{_port}/webhook");
        req.Headers.Add("Origin", "https://from-static.com");
        var res = await _client.SendAsync(req);

        res.Headers.GetValues("Access-Control-Allow-Origin").Single().Should().Be("https://from-resolver.com");
    }

    [Fact]
    public async Task Resolver_ReturnsNull_NoCorsHeaders()
    {
        Func<HttpRequest, string?> resolver = _ => null;

        await RegisterAsync("/webhook", new RouteCorsOptions(
            AllowedOrigins: null,
            AllowedMethods: null,
            AllowCredentials: false,
            OriginsResolver: resolver));

        var req = new HttpRequestMessage(System.Net.Http.HttpMethod.Get, $"http://127.0.0.1:{_port}/webhook");
        req.Headers.Add("Origin", "https://anything.com");
        var res = await _client.SendAsync(req);

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        res.Headers.Contains("Access-Control-Allow-Origin").Should().BeFalse();
    }

    [Fact]
    public async Task Resolver_ReceivesActualRequest()
    {
        // Capture extracted values, not the HttpRequest itself -- Kestrel pools HttpContext
        // and the request features become disposed once the response completes.
        string? capturedPath = null;
        string? capturedMethod = null;
        string? capturedOrigin = null;
        Func<HttpRequest, string?> resolver = req =>
        {
            capturedPath = req.Path.Value;
            capturedMethod = req.Method;
            capturedOrigin = req.Headers["Origin"];
            return capturedOrigin;
        };

        await RegisterAsync("/webhook", new RouteCorsOptions(
            AllowedOrigins: null,
            AllowedMethods: null,
            AllowCredentials: false,
            OriginsResolver: resolver));

        var req = new HttpRequestMessage(System.Net.Http.HttpMethod.Get, $"http://127.0.0.1:{_port}/webhook");
        req.Headers.Add("Origin", "https://app.example.com");
        await _client.SendAsync(req);

        capturedPath.Should().Be("/webhook");
        capturedMethod.Should().Be("GET");
        capturedOrigin.Should().Be("https://app.example.com");
    }

    // ── Preflight reflection ──

    [Fact]
    public async Task Preflight_EchoesRequestedHeadersAndMethod()
    {
        await RegisterAsync("/webhook", new RouteCorsOptions(
            AllowedOrigins: "https://app.com",
            AllowedMethods: "GET,POST",
            AllowCredentials: false),
            methods: "GET,POST");

        var req = new HttpRequestMessage(System.Net.Http.HttpMethod.Options, $"http://127.0.0.1:{_port}/webhook");
        req.Headers.Add("Origin", "https://app.com");
        req.Headers.Add("Access-Control-Request-Method", "POST");
        req.Headers.Add("Access-Control-Request-Headers", "Authorization, X-Custom");
        var res = await _client.SendAsync(req);

        res.StatusCode.Should().Be(HttpStatusCode.NoContent);
        res.Headers.GetValues("Access-Control-Allow-Methods").Single().Should().Be("POST");
        res.Headers.GetValues("Access-Control-Allow-Headers").Single().Should().Be("Authorization, X-Custom");
        res.Headers.GetValues("Access-Control-Max-Age").Single().Should().Be("86400");
    }

    [Fact]
    public async Task Preflight_DisallowedOrigin_Returns204_NoCorsHeaders()
    {
        await RegisterAsync("/webhook", new RouteCorsOptions(
            AllowedOrigins: "https://app.com",
            AllowedMethods: null,
            AllowCredentials: false));

        var req = new HttpRequestMessage(System.Net.Http.HttpMethod.Options, $"http://127.0.0.1:{_port}/webhook");
        req.Headers.Add("Origin", "https://evil.com");
        req.Headers.Add("Access-Control-Request-Method", "POST");
        var res = await _client.SendAsync(req);

        // Preflight must always 204 (browser-friendly), but no Allow-Origin tells the browser
        // to fail the actual request without retrying.
        res.StatusCode.Should().Be(HttpStatusCode.NoContent);
        res.Headers.Contains("Access-Control-Allow-Origin").Should().BeFalse();
    }

    // ── Wildcard + credentials guard ──

    [Fact]
    public async Task Wildcard_WithCredentials_DemotedToNoHeaders()
    {
        // Static-whitelist case: validation in HttpEndpointOptions blocks this at config time
        // for the consumer-API path, but RouteCorsOptions is also reachable directly via
        // SharedHttpServerManager.RegisterRoute, so the request-time guard is the last line of
        // defence. We test a resolver that returns "*" to exercise that path.
        Func<HttpRequest, string?> resolver = _ => "*";

        await RegisterAsync("/webhook", new RouteCorsOptions(
            AllowedOrigins: null,
            AllowedMethods: null,
            AllowCredentials: true,
            OriginsResolver: resolver));

        var req = new HttpRequestMessage(System.Net.Http.HttpMethod.Get, $"http://127.0.0.1:{_port}/webhook");
        req.Headers.Add("Origin", "https://app.com");
        var res = await _client.SendAsync(req);

        res.Headers.Contains("Access-Control-Allow-Origin").Should().BeFalse();
        res.Headers.Contains("Access-Control-Allow-Credentials").Should().BeFalse();
    }

    // ── Per-route dispatch ──

    [Fact]
    public async Task TwoRoutes_SamePort_DifferentPolicies_DoNotCollide()
    {
        // Route A: public wildcard
        await RegisterAsync("/public", new RouteCorsOptions(
            AllowedOrigins: "*",
            AllowedMethods: null,
            AllowCredentials: false));

        // Route B: strict whitelist with credentials
        await RegisterAsync("/private", new RouteCorsOptions(
            AllowedOrigins: "https://trusted.com",
            AllowedMethods: null,
            AllowCredentials: true));

        var pub = new HttpRequestMessage(System.Net.Http.HttpMethod.Get, $"http://127.0.0.1:{_port}/public");
        pub.Headers.Add("Origin", "https://anything.com");
        var pubRes = await _client.SendAsync(pub);

        pubRes.Headers.GetValues("Access-Control-Allow-Origin").Single().Should().Be("*");
        pubRes.Headers.Contains("Access-Control-Allow-Credentials").Should().BeFalse();

        var priv = new HttpRequestMessage(System.Net.Http.HttpMethod.Get, $"http://127.0.0.1:{_port}/private");
        priv.Headers.Add("Origin", "https://trusted.com");
        var privRes = await _client.SendAsync(priv);

        privRes.Headers.GetValues("Access-Control-Allow-Origin").Single().Should().Be("https://trusted.com");
        privRes.Headers.GetValues("Access-Control-Allow-Credentials").Single().Should().Be("true");

        // Route B from an untrusted origin: no CORS headers, even though Route A on the same
        // port would have allowed any origin. This is the bug-fix the per-route dispatcher closes.
        var privEvil = new HttpRequestMessage(System.Net.Http.HttpMethod.Get, $"http://127.0.0.1:{_port}/private");
        privEvil.Headers.Add("Origin", "https://evil.com");
        var privEvilRes = await _client.SendAsync(privEvil);

        privEvilRes.Headers.Contains("Access-Control-Allow-Origin").Should().BeFalse();
    }

    [Fact]
    public async Task RouteWithoutCors_DoesNotEmitHeaders_EvenWhenSiblingHasCors()
    {
        // Sibling on the same port enables CORS, which causes the dispatch middleware to be
        // installed -- but the no-CORS route must remain transparent (no headers emitted).
        await RegisterAsync("/with-cors", new RouteCorsOptions(
            AllowedOrigins: "*",
            AllowedMethods: null,
            AllowCredentials: false));

        var noCors = _serverManager.RegisterRoute(
            "127.0.0.1", _port, "/no-cors", null,
            ctx => { ctx.Response.StatusCode = 200; return Task.CompletedTask; });
        _registrations.Add(noCors);
        await _serverManager.EnsureStarted("127.0.0.1", _port);

        var req = new HttpRequestMessage(System.Net.Http.HttpMethod.Get, $"http://127.0.0.1:{_port}/no-cors");
        req.Headers.Add("Origin", "https://anything.com");
        var res = await _client.SendAsync(req);

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        res.Headers.Contains("Access-Control-Allow-Origin").Should().BeFalse();
    }

    private static int GetFreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}

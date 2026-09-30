using System.Net;
using redb.Route.Core;
using redb.Route.Http;

namespace redb.Route.Tests.Http;

/// <summary>
/// The consumer does not echo the request's headers back, but a header the route wrote goes out even when the
/// client sent one with the same name. Filtering by name let a client suppress the route's headers: sending
/// <c>Cache-Control</c> in the request removed <c>Cache-Control: no-store</c> from a token response.
/// </summary>
[Collection("HttpServer")]
public class HttpResponseHeaderOverrideTests : IAsyncLifetime
{
    private int _port;
    private HttpClient _client = null!;
    private RouteContext _ctx = null!;
    private SharedHttpServerManager _servers = null!;

    public async Task InitializeAsync()
    {
        _port = global::redb.Route.Tests.Shared.TestPorts.Next();
        _client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_port}") };
        _servers = new SharedHttpServerManager();
        _ctx = new RouteContext();
        _ctx.AddComponent(new HttpComponent { ServerManager = _servers });
        var port = _port;
        _ctx.AddRoutes(r =>
        {
            r.From($"http://127.0.0.1:{port}/token?inOut=true")
                .Process(e =>
                {
                    e.In.Headers["Cache-Control"] = "no-store";
                    e.In.Headers["X-Route"] = "route";
                })
                .SetBody("{\"access_token\":\"t\"}");
            r.From($"http://127.0.0.1:{port}/seen?inOut=true")
                .Process(e => e.In.Body = string.Join(",", ResponseOnly.Where(e.In.Headers.ContainsKey)));
        });
        await _ctx.Start();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _ctx.DisposeAsync();
        await _servers.DisposeAsync();
    }

    private static readonly string[] ResponseOnly =
        ["Set-Cookie", "Location", "WWW-Authenticate", "Proxy-Authenticate", "Authentication-Info", "Retry-After", "Server", "ETag", "Vary"];

    [Fact]
    public async Task Response_only_headers_in_a_request_never_reach_the_route_nor_the_response()
    {
        var request = new HttpRequestMessage(System.Net.Http.HttpMethod.Get, "/seen");
        foreach (var name in ResponseOnly)
            request.Headers.TryAddWithoutValidation(name, name == "Set-Cookie" ? "session=attacker" : "x");

        var response = await _client.SendAsync(request);

        (await response.Content.ReadAsStringAsync()).Should().BeEmpty("a request has no business carrying response fields");
        response.Headers.Contains("Set-Cookie").Should().BeFalse();
        response.Headers.Location.Should().BeNull();
    }

    private Task<HttpResponseMessage> Get(params (string Name, string Value)[] headers) => Send("/token", headers);

    private async Task<HttpResponseMessage> Send(string path, params (string Name, string Value)[] headers)
    {
        var request = new HttpRequestMessage(System.Net.Http.HttpMethod.Get, path);
        foreach (var (name, value) in headers)
            request.Headers.TryAddWithoutValidation(name, value);
        return await _client.SendAsync(request);
    }

    [Fact]
    public async Task A_header_the_route_wrote_goes_out_although_the_client_sent_its_name()
    {
        var response = await Get(("Cache-Control", "max-age=3600"), ("X-Route", "client"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.CacheControl!.NoStore.Should().BeTrue("the client must not be able to switch off no-store");
        response.Headers.GetValues("X-Route").Should().Equal("route");
    }

    [Fact]
    public async Task A_header_the_route_wrote_with_the_clients_value_still_goes_out()
    {
        var response = await Get(("Cache-Control", "no-store"));

        response.Headers.CacheControl!.NoStore.Should().BeTrue();
    }

    [Fact]
    public async Task A_request_header_the_route_left_alone_is_not_echoed()
    {
        var response = await Get(("X-Client-Only", "secret-ish"));

        response.Headers.Contains("X-Client-Only").Should().BeFalse();
    }
}

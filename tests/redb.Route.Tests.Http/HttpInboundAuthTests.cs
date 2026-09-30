using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Http;
using redb.Route.Http.Rest;

namespace redb.Route.Tests.Http;

/// <summary>
/// Inbound authentication on an http: consumer and on a REST declaration: Basic against configured credentials,
/// Bearer through a registered <see cref="IHttpTokenValidator"/>. A refused request gets 401 with
/// <c>WWW-Authenticate</c> and never reaches the route; an accepted one reaches it with the principal on the exchange
/// and without the <c>Authorization</c> header.
/// </summary>
[Collection("HttpServer")]
public class HttpInboundAuthTests : IAsyncLifetime
{
    private sealed class TokenValidator : IHttpTokenValidator
    {
        public Task<ClaimsPrincipal?> ValidateAsync(string token, CancellationToken ct)
            => Task.FromResult(token == "good-token"
                ? new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "svc-orders")], "Bearer"))
                : null);
    }

    /// <summary>What the route saw: the principal and whether an Authorization header was still there.</summary>
    private sealed record Seen(string? User, bool AuthorizationHeader);

    private readonly List<Seen> _seen = [];
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
        _ctx.AddToRegistry("tokens", new TokenValidator());
        var port = _port;
        _ctx.AddRoutes(r =>
        {
            r.From($"http://127.0.0.1:{port}/basic?inboundAuth=basic&inboundUsername=admin&inboundPassword=s3cret&inboundRealm=orders")
                .Process(Remember).SetBody("ok");
            r.From($"http://127.0.0.1:{port}/bearer?inboundAuth=bearer&tokenValidator=#tokens")
                .Process(Remember).SetBody("ok");
            r.Rest("/api", o =>
            {
                o.Host = "127.0.0.1"; o.Port = port; o.OpenApi = false;
                o.InboundAuth = HttpAuthScheme.Bearer; o.TokenValidator = "#tokens";
            }).Get("/orders").Route().Process(Remember).SetBody("orders");
        });
        await _ctx.Start();
    }

    private void Remember(IExchange exchange)
    {
        lock (_seen)
            _seen.Add(new Seen(ExchangePrincipal.Get(exchange)?.Identity?.Name, exchange.In.Headers.ContainsKey("Authorization")));
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _ctx.DisposeAsync();
        await _servers.DisposeAsync();
    }

    private Task<HttpResponseMessage> Get(string path, AuthenticationHeaderValue? auth)
    {
        var request = new HttpRequestMessage(System.Net.Http.HttpMethod.Get, path);
        request.Headers.Authorization = auth;
        return _client.SendAsync(request);
    }

    private static AuthenticationHeaderValue Basic(string user, string password)
        => new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}")));

    // ── Basic ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Basic_without_credentials_is_401_with_the_challenge()
    {
        var response = await Get("/basic", null);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        response.Headers.WwwAuthenticate.ToString().Should().Be("Basic realm=\"orders\", charset=\"UTF-8\"");
        _seen.Should().BeEmpty("a refused request never reaches the route");
    }

    [Theory]
    [InlineData("admin", "wrong")]
    [InlineData("root", "s3cret")]
    [InlineData("admin", "")]
    public async Task Basic_with_wrong_credentials_is_401(string user, string password)
    {
        (await Get("/basic", Basic(user, password))).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        _seen.Should().BeEmpty();
    }

    [Fact]
    public async Task Basic_that_is_not_base64_is_401()
    {
        (await Get("/basic", new AuthenticationHeaderValue("Basic", "***"))).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Basic_with_the_right_credentials_reaches_the_route_with_the_principal_and_without_the_header()
    {
        var response = await Get("/basic", Basic("admin", "s3cret"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        _seen.Should().ContainSingle().Which.Should().Be(new Seen("admin", AuthorizationHeader: false));
    }

    [Fact]
    public async Task Basic_does_not_accept_a_bearer_token()
    {
        (await Get("/basic", new AuthenticationHeaderValue("Bearer", "good-token"))).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── Bearer ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Bearer_without_a_token_is_401_with_the_challenge()
    {
        var response = await Get("/bearer", null);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        response.Headers.WwwAuthenticate.ToString().Should().StartWith("Bearer realm=");
    }

    [Fact]
    public async Task Bearer_refused_by_the_validator_is_401_invalid_token()
    {
        var response = await Get("/bearer", new AuthenticationHeaderValue("Bearer", "forged"));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        response.Headers.WwwAuthenticate.ToString().Should().Contain("error=\"invalid_token\"");
        _seen.Should().BeEmpty();
    }

    [Fact]
    public async Task Bearer_accepted_by_the_validator_reaches_the_route_with_its_principal()
    {
        (await Get("/bearer", new AuthenticationHeaderValue("Bearer", "good-token"))).StatusCode.Should().Be(HttpStatusCode.OK);

        _seen.Should().ContainSingle().Which.Should().Be(new Seen("svc-orders", AuthorizationHeader: false));
    }

    [Fact]
    public async Task A_rest_declaration_protects_its_verbs()
    {
        (await Get("/api/orders", null)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await Get("/api/orders", new AuthenticationHeaderValue("Bearer", "good-token"))).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ── Declaration errors ───────────────────────────────────────────────────

    private static async Task<Exception?> StartWith(string uri, Action<RouteContext>? arrange = null)
    {
        await using var servers = new SharedHttpServerManager();
        await using var ctx = new RouteContext();
        ctx.AddComponent(new HttpComponent { ServerManager = servers });
        arrange?.Invoke(ctx);
        ctx.AddRoutes(r => r.From(uri).SetBody("x"));
        return await Record.ExceptionAsync(() => ctx.Start());
    }

    private static string Uri(string query)
    {
        var port = global::redb.Route.Tests.Shared.TestPorts.Next();
        return $"http://127.0.0.1:{port}/x?{query}";
    }

    [Fact]
    public async Task An_unregistered_token_validator_stops_the_start()
        => (await StartWith(Uri("inboundAuth=bearer&tokenValidator=#nobody")))!.ToString().Should().Contain("nobody");

    [Theory]
    [InlineData("inboundAuth=basic&inboundUsername=admin", "inboundPassword")]
    [InlineData("inboundAuth=bearer", "tokenValidator")]
    [InlineData("inboundUsername=admin&inboundPassword=x", "inboundAuth")]
    [InlineData("tokenValidator=#tokens", "inboundAuth")]
    [InlineData("inboundAuth=basic&inboundUsername=a&inboundPassword=b&tokenValidator=#tokens", "tokenValidator")]
    [InlineData("inboundAuth=basic&inboundUsername=a&inboundPassword=b&inboundRealm=a\"b", "inboundRealm")]
    public async Task An_incomplete_or_contradictory_declaration_stops_the_start(string query, string named)
        => (await StartWith(Uri(query)))!.ToString().Should().Contain(named);

    [Fact]
    public async Task Inbound_options_on_a_producer_are_refused()
    {
        await using var ctx = new RouteContext();
        ctx.AddComponent(new HttpComponent());
        var endpoint = ctx.GetEndpoint("http://127.0.0.1:1/x?inboundAuth=basic&inboundUsername=a&inboundPassword=b");

        var act = () => endpoint.CreateProducer();

        act.Should().Throw<InvalidOperationException>().WithMessage("*inboundAuth*consumer*")
            .Which.Message.Should().NotContain("inboundPassword=b");
    }
}

/// <summary>The side-only options of the http: endpoint carry their role as a declaration tooling can read.</summary>
public class HttpEndpointRoleDeclarationTests
{
    [Theory]
    [InlineData(nameof(HttpEndpointOptions.AuthScheme), EndpointRole.Producer)]
    [InlineData(nameof(HttpEndpointOptions.Username), EndpointRole.Producer)]
    [InlineData(nameof(HttpEndpointOptions.Password), EndpointRole.Producer)]
    [InlineData(nameof(HttpEndpointOptions.AuthToken), EndpointRole.Producer)]
    [InlineData(nameof(HttpEndpointOptions.InboundAuth), EndpointRole.Consumer)]
    [InlineData(nameof(HttpEndpointOptions.InboundUsername), EndpointRole.Consumer)]
    [InlineData(nameof(HttpEndpointOptions.InboundPassword), EndpointRole.Consumer)]
    [InlineData(nameof(HttpEndpointOptions.InboundRealm), EndpointRole.Consumer)]
    [InlineData(nameof(HttpEndpointOptions.TokenValidator), EndpointRole.Consumer)]
    public void The_option_declares_its_role(string property, EndpointRole role)
        => EndpointOptions.RoleOf(typeof(HttpEndpointOptions).GetProperty(property)!).Should().Be(role);
}

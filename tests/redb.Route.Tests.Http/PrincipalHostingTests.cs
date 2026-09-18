using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Http;

namespace redb.Route.Tests.Http;

/// <summary>
/// The caller's identity through the real shared host: <see cref="HttpHostingOptions.ResolvePrincipal"/>
/// identifies the request and <see cref="HttpConsumer"/> puts the principal on the exchange
/// (<see cref="ExchangePrincipal"/>).
/// <para>
/// Before this nothing could bring an identity to an HTTP route. The consumer ignored who the request
/// came from, and a trusted middleware had no channel either: a <c>redbHttp.*</c> header it added is
/// dropped by the anti-spoofing guard, and any other header cannot be told apart from one the caller
/// sent.
/// </para>
/// </summary>
[Collection("HttpServer")]
public class PrincipalHostingTests : IAsyncLifetime
{
    private const string TokenHeader = "X-Test-Token";
    private const string Client = "203.0.113.7";

    private HttpClient _http = null!;
    private int _port;
    private SharedHttpServerManager? _serverManager;
    private HttpConsumer? _consumer;
    private IExchange? _captured;
    private int _routeRuns;
    private readonly CapturingLogger _logger = new();

    public Task InitializeAsync()
    {
        _port = GetFreePort();
        _http = new HttpClient();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        if (_consumer is not null) await _consumer.Stop();
        if (_serverManager is not null) await _serverManager.DisposeAsync();
    }

    private async Task StartAsync(HttpHostingOptions? options)
    {
        _serverManager = new SharedHttpServerManager(options, _logger);

        var component = new HttpComponent();
        var parameters = new Dictionary<string, string> { ["host"] = "127.0.0.1", ["port"] = _port.ToString() };
        var uriPath = $"/127.0.0.1:{_port}/probe";
        var endpoint = (HttpEndpoint)component.CreateEndpoint(new EndpointUri("http", uriPath, $"http:{uriPath}", parameters));

        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                Interlocked.Increment(ref _routeRuns);
                _captured = ci.Arg<IExchange>();
                return Task.CompletedTask;
            });

        _consumer = new HttpConsumer(endpoint, processor, endpoint.EndpointOptions, _serverManager);
        await _consumer.Start();
    }

    private async Task<HttpResponseMessage> SendAsync(Action<HttpRequestMessage>? configure = null)
    {
        var req = new HttpRequestMessage(System.Net.Http.HttpMethod.Get, $"http://127.0.0.1:{_port}/probe");
        configure?.Invoke(req);
        return await _http.SendAsync(req);
    }

    private static ClaimsPrincipal User(string id) =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, id)], "test"));

    /// <summary>A resolver that knows one token; anything else is an anonymous caller.</summary>
    private static Func<HttpContext, Task<ClaimsPrincipal?>> TokenResolver => ctx =>
        Task.FromResult(ctx.Request.Headers[TokenHeader].ToString() == "good" ? User("user-42") : null);

    private static HttpHostingOptions WithResolver(Func<HttpContext, Task<ClaimsPrincipal?>> resolve)
    {
        var o = new HttpHostingOptions { ResolvePrincipal = resolve };
        return o;
    }

    // ── Default host ──

    [Fact]
    public async Task NoResolver_ExchangeCarriesNoIdentity()
    {
        await StartAsync(null);

        var res = await SendAsync(r => r.Headers.Add(TokenHeader, "good"));

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        ExchangePrincipal.Get(_captured!).Should().BeNull("nobody asked the host to identify callers");
    }

    // ── Resolver configured ──

    [Fact]
    public async Task IdentifiedCaller_PrincipalReachesTheExchange()
    {
        await StartAsync(WithResolver(TokenResolver));

        var res = await SendAsync(r => r.Headers.Add(TokenHeader, "good"));

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        ExchangePrincipal.Get(_captured!)!.FindFirst(ClaimTypes.NameIdentifier)!.Value.Should().Be("user-42");
    }

    [Fact]
    public async Task AnonymousCaller_IsServed_WithoutIdentity()
    {
        await StartAsync(WithResolver(TokenResolver));

        var res = await SendAsync();

        res.StatusCode.Should().Be(HttpStatusCode.OK,
            "the host identifies, it does not authorize: whether anonymous is acceptable is the route's call");
        _routeRuns.Should().Be(1);
        ExchangePrincipal.Get(_captured!).Should().BeNull();
    }

    [Fact]
    public async Task ResolverThatThrows_FailsTheRequest_RouteNeverRuns_ErrorIsLogged()
    {
        await StartAsync(WithResolver(_ => throw new InvalidOperationException("key endpoint https://idp.internal unreachable")));

        var res = await SendAsync(r => r.Headers.Add(TokenHeader, "good"));

        res.StatusCode.Should().Be(HttpStatusCode.InternalServerError,
            "a resolver that could not decide must not turn the caller into an anonymous one");
        _routeRuns.Should().Be(0);
        (await res.Content.ReadAsStringAsync()).Should().NotContain("idp.internal", "the resolver's failure text is ours, not the caller's");
        _logger.Entries.Should().Contain(e => e.Level == LogLevel.Error && e.Exception is InvalidOperationException);
    }

    [Fact]
    public async Task Resolver_SeesTheClientResolvedByTrustedProxies()
    {
        IPAddress? seen = null;
        var o = WithResolver(ctx =>
        {
            seen = ctx.Connection.RemoteIpAddress;
            return Task.FromResult<ClaimsPrincipal?>(null);
        });
        o.TrustedProxies.Add("127.0.0.1");
        await StartAsync(o);

        await SendAsync(r => r.Headers.TryAddWithoutValidation(ForwardedHeaderResolver.ForwardedFor, Client));

        seen.Should().Be(IPAddress.Parse(Client),
            "an address-aware resolver has to see the client, not the proxy in front of it");
    }

    [Fact]
    public async Task Caller_CannotForgeTheIdentity_WithHeadersNamedLikeTheKeys()
    {
        await StartAsync(WithResolver(TokenResolver));

        var res = await SendAsync(r =>
        {
            r.Headers.TryAddWithoutValidation(ExchangePrincipal.PropertyKey, "admin");
            r.Headers.TryAddWithoutValidation(SharedHttpServerManager.PrincipalItem, "admin");
        });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        ExchangePrincipal.Get(_captured!).Should().BeNull("identity is a property set in-process, never a header from the wire");
    }

    [Fact]
    public async Task CorsPreflight_NeverReachesTheResolver()
    {
        var calls = 0;
        _serverManager = new SharedHttpServerManager(WithResolver(_ =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult<ClaimsPrincipal?>(null);
        }), _logger);

        var registration = _serverManager.RegisterRoute("127.0.0.1", _port, "/cors", null,
            ctx => { ctx.Response.StatusCode = 200; return Task.CompletedTask; },
            corsOptions: new RouteCorsOptions(AllowedOrigins: "https://example.com", AllowedMethods: null, AllowCredentials: false));
        try
        {
            await _serverManager.EnsureStarted("127.0.0.1", _port);

            var preflight = new HttpRequestMessage(System.Net.Http.HttpMethod.Options, $"http://127.0.0.1:{_port}/cors");
            preflight.Headers.Add("Origin", "https://example.com");
            preflight.Headers.Add("Access-Control-Request-Method", "POST");
            (await _http.SendAsync(preflight)).StatusCode.Should().Be(HttpStatusCode.NoContent);
            calls.Should().Be(0, "a preflight carries no credentials and is answered before identity is resolved");

            (await _http.GetAsync($"http://127.0.0.1:{_port}/cors")).StatusCode.Should().Be(HttpStatusCode.OK);
            calls.Should().Be(1, "the actual request does reach the resolver");
        }
        finally
        {
            _serverManager.UnregisterRoute(registration);
        }
    }

    private sealed class CapturingLogger : ILogger
    {
        private readonly List<(LogLevel Level, Exception? Exception)> _entries = [];

        public IReadOnlyList<(LogLevel Level, Exception? Exception)> Entries
        {
            get { lock (_entries) return _entries.ToArray(); }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_entries) _entries.Add((logLevel, exception));
        }
    }

    private static int GetFreePort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }
}

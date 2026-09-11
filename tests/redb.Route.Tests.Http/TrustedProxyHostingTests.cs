using System.Net;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Http;

namespace redb.Route.Tests.Http;

/// <summary>
/// End to end through the real host: a request with forwarded headers arrives at Kestrel, the
/// <see cref="SharedHttpServerManager"/> resolves the client against its trusted proxies, and the
/// <see cref="HttpConsumer"/> writes what it sees into the exchange. The assertions are on the
/// exchange headers (<c>redbHttp.RemoteAddress</c>, <c>redbHttp.Url</c>) because that is what a
/// throttle, an audit record or a DPoP check actually reads.
/// </summary>
/// <remarks>
/// The socket peer here is always <c>127.0.0.1</c>, so "trusted peer" means the loopback address
/// is in the list and "untrusted peer" means it is not. Every test was run against the host with
/// no resolver installed first, where the two-hop, single-hop and scheme cases fail.
/// </remarks>
[Collection("HttpServer")]
public class TrustedProxyHostingTests : IAsyncLifetime
{
    private const string Client = "203.0.113.7";
    private const string Edge = "10.0.0.9";

    private HttpClient _http = null!;
    private int _port;
    private SharedHttpServerManager? _serverManager;
    private HttpConsumer? _consumer;
    private IExchange? _captured;

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
        _serverManager = new SharedHttpServerManager(options);

        var component = new HttpComponent();
        var parameters = new Dictionary<string, string> { ["host"] = "127.0.0.1", ["port"] = _port.ToString() };
        var uriPath = $"/127.0.0.1:{_port}/probe";
        var endpoint = (HttpEndpoint)component.CreateEndpoint(new EndpointUri("http", uriPath, $"http:{uriPath}", parameters));

        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(ci => { _captured = ci.Arg<IExchange>(); return Task.CompletedTask; });

        _consumer = new HttpConsumer(endpoint, processor, endpoint.EndpointOptions, _serverManager);
        await _consumer.Start();
    }

    private async Task<IExchange> SendAsync(string? forwardedFor, string? forwardedProto = null)
    {
        var req = new HttpRequestMessage(System.Net.Http.HttpMethod.Get, $"http://127.0.0.1:{_port}/probe");
        if (forwardedFor is not null) req.Headers.TryAddWithoutValidation(ForwardedHeaderResolver.ForwardedFor, forwardedFor);
        if (forwardedProto is not null) req.Headers.TryAddWithoutValidation(ForwardedHeaderResolver.ForwardedProto, forwardedProto);

        var res = await _http.SendAsync(req);
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        _captured.Should().NotBeNull();
        return _captured!;
    }

    private static string RemoteAddress(IExchange ex) => ex.In.Headers[HttpHeaders.RemoteAddress]!.ToString()!;
    private static string Url(IExchange ex) => ex.In.Headers[HttpHeaders.Url]!.ToString()!;

    private static HttpHostingOptions TrustLoopbackAnd(params string[] more)
    {
        var o = new HttpHostingOptions();
        o.TrustedProxies.Add("127.0.0.1");
        foreach (var m in more) o.TrustedProxies.Add(m);
        return o;
    }

    // ── Default host: nothing is trusted ──

    [Fact]
    public async Task DefaultHost_ForwardedHeadersIgnored_SocketIsClient()
    {
        await StartAsync(null);

        var ex = await SendAsync(Client, "https");

        RemoteAddress(ex).Should().Be("127.0.0.1");
        Url(ex).Should().StartWith("http://");
    }

    [Fact]
    public async Task UntrustedPeer_ForwardedHeadersIgnored()
    {
        var o = new HttpHostingOptions();
        o.TrustedProxies.Add("192.168.100.100"); // not the loopback peer
        await StartAsync(o);

        var ex = await SendAsync(Client, "https");

        RemoteAddress(ex).Should().Be("127.0.0.1");
        Url(ex).Should().StartWith("http://");
    }

    // ── Trusted peer ──

    [Fact]
    public async Task TrustedPeer_SingleHop_ClientReachesExchange()
    {
        await StartAsync(TrustLoopbackAnd());

        var ex = await SendAsync(Client);

        RemoteAddress(ex).Should().Be(Client);
    }

    [Fact]
    public async Task TrustedPeer_TwoHopChain_ClientReachesExchange_NotTheEdge()
    {
        // client -> edge (10.0.0.9) -> peer (127.0.0.1) -> us. The right-most entry is the edge.
        await StartAsync(TrustLoopbackAnd(Edge));

        var ex = await SendAsync($"{Client}, {Edge}");

        RemoteAddress(ex).Should().Be(Client, "the edge is a trusted proxy, not the client");
    }

    [Fact]
    public async Task TrustedPeer_UnparseableHop_FallsBackToSocket()
    {
        await StartAsync(TrustLoopbackAnd());

        var ex = await SendAsync($"{Client}, unknown");

        RemoteAddress(ex).Should().Be("127.0.0.1", "a chain that cannot be read is not trusted for the address");
    }

    [Fact]
    public async Task TrustedPeer_ChainEntirelyTrusted_KeepsSocket()
    {
        await StartAsync(TrustLoopbackAnd(Edge));

        var ex = await SendAsync(Edge);

        RemoteAddress(ex).Should().Be("127.0.0.1");
    }

    [Fact]
    public async Task TrustedPeer_ForwardedProto_RewritesUrlScheme()
    {
        await StartAsync(TrustLoopbackAnd());

        var ex = await SendAsync(Client, "https");

        Url(ex).Should().StartWith($"https://127.0.0.1:{_port}/probe");
        RemoteAddress(ex).Should().Be(Client);
    }

    [Fact]
    public async Task TrustedPeer_ForwardSchemeOff_KeepsSocketScheme()
    {
        var o = TrustLoopbackAnd();
        o.TrustedProxies.ForwardScheme = false;
        await StartAsync(o);

        var ex = await SendAsync(Client, "https");

        Url(ex).Should().StartWith("http://");
        RemoteAddress(ex).Should().Be(Client, "the address is still resolved; only the scheme is left alone");
    }

    [Fact]
    public async Task ClientCannotPickItsOwnBucket_ByPrependingToTheChain()
    {
        // Whatever the client puts in the header lands to the LEFT of what the trusted peer sees.
        // Here the "peer" is loopback and there is no real proxy appending, so the whole header is
        // client-supplied; the right-most entry is what the trusted peer would have appended and
        // is the only entry honoured.
        await StartAsync(TrustLoopbackAnd());

        var ex = await SendAsync("1.2.3.4, 5.6.7.8");

        RemoteAddress(ex).Should().Be("5.6.7.8");
        RemoteAddress(ex).Should().NotBe("1.2.3.4");
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

using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Http;

namespace redb.Route.Tests.Http;

/// <summary>
/// What crosses an HTTP request and its response today, all four points: pinned before the header filter strategy
/// (docs/V4/headers/01-HEADER-FILTER-STRATEGY.md) takes the rule over, so the transport's default keeps it exactly.
/// <list type="bullet">
/// <item>producer, request: every exchange header but the control and protocol headers and empty values; <c>skipRequestHeaders=true</c>
/// sends none;</item>
/// <item>consumer, request: every request header but HTTP/2 pseudo-headers, <c>redbHttp.*</c> and response-only
/// fields;</item>
/// <item>consumer, response: every exchange header but the non-bridged ones, <c>redb*</c>/<c>Camel*</c> and the values
/// the request brought;</item>
/// <item>producer, response: every response header (unless <c>skipResponseHeaders=true</c>).</item>
/// </list>
/// Plus what the inventory found: a <c>redbHttp.*</c> header the list does not name crosses as a request header
/// (finding 3.3), and a <c>redbHttp.Method</c> on the exchange, wherever it came from, sets the method (finding 3.1).
/// </summary>
[Collection("HttpServer")]
public sealed class HttpHeaderMappingCharacterizationTests : IAsyncLifetime
{
    private int _port;
    private RouteContext _ctx = null!;
    private SharedHttpServerManager _servers = null!;
    private readonly TaskCompletionSource<Dictionary<string, object?>> _received = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task InitializeAsync()
    {
        _port = global::redb.Route.Tests.Shared.TestPorts.Next();
        _servers = new SharedHttpServerManager();
        _ctx = new RouteContext();
        _ctx.AddComponent(new HttpComponent { ServerManager = _servers });
        var port = _port;
        _ctx.AddRoutes(r => r.From($"http://127.0.0.1:{port}/hdr?inOut=true&methods=GET,POST,DELETE").Process(e =>
        {
            _received.TrySetResult(new Dictionary<string, object?>(e.In.Headers, StringComparer.OrdinalIgnoreCase));
            e.In.Headers.Clear();
            e.In.Headers["X-Reply"] = "r1";
            e.In.Headers["X-Int"] = 7;
            e.In.Headers["X-Null"] = null;
            e.In.Headers["redbInternal"] = "never";
            e.In.Headers["CamelSomething"] = "never";
            e.In.Body = "reply";
        }));
        _ctx.AddRoutes(r => r.From($"http://127.0.0.1:{port}/echo?inOut=true&methods=POST").Process(e =>
        {
            e.In.Headers["X-Reply"] = "r1";
            e.In.Body = "reply";
        }));
        await _ctx.Start();
    }

    public async Task DisposeAsync()
    {
        await _ctx.DisposeAsync();
        await _servers.DisposeAsync();
    }

    private async Task<IExchange> Send(Action<IExchange> headers, string? extra = null)
    {
        var producer = _ctx.GetEndpoint($"http://127.0.0.1:{_port}/hdr?{extra ?? "method=POST"}").CreateProducer();
        await producer.Start();
        var exchange = new Exchange(new Message("body"));
        headers(exchange);
        await producer.Process(exchange);
        await producer.Stop();
        return exchange;
    }

    [Fact]
    public async Task RequestAndResponse_TheHeadersThatCross()
    {
        var sent = await Send(e =>
        {
            e.In.Headers["X-Corr"] = "c1";
            e.In.Headers["X-Int"] = 42;
            e.In.Headers["X-Null"] = null;
            e.In.Headers["Authorization"] = "Bearer secret";
            e.In.Headers["Connection"] = "close-me";
            e.In.Headers[HttpHeaders.QueryParamPrefix + "sort"] = "asc";
        });

        var request = await _received.Task.WaitAsync(TimeSpan.FromSeconds(10));
        request["X-Corr"].Should().Be("c1");
        request["X-Int"].Should().Be("42", "a value crosses as its text");
        request.Should().NotContainKey("X-Null", "a null value is skipped");
        request["Authorization"].Should().Be("Bearer secret", "nothing filters a credential (Camel does not either)");
        request.Should().NotContainKey("Connection", "a hop-by-hop header is not bridged");
        request.Should().NotContainKey(HttpHeaders.QueryParamPrefix + "sort",
            "the consumer drops every redbHttp.* it receives (the producer sent it: see finding 3.3)");

        var response = (sent.HasOut ? sent.Out! : sent.In).Headers;
        response["X-Reply"].Should().Be("r1");
        response["X-Int"].Should().Be("7");
        response.Should().NotContainKey("X-Null");
        response.Should().NotContainKey("redbInternal", "the consumer does not answer with redb* headers");
        response.Should().NotContainKey("CamelSomething", "nor with Camel* ones");
    }

    [Fact]
    public async Task ARedbHttpHeaderTheListDoesNotName_CrossesAsARequestHeader()
    {
        // Finding 3.3: on the wire under its own name; the consumer then drops it as a redbHttp.* header, so it is
        // seen on the wire only. Pinned on the producer side through the request the server received.
        string? onTheWire = null;
        await using var ctx = new RouteContext();
        await using var servers = new SharedHttpServerManager();
        ctx.AddComponent(new HttpComponent { ServerManager = servers });
        var probePort = global::redb.Route.Tests.Shared.TestPorts.Next();
        using var listener = new System.Net.HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{probePort}/");
        listener.Start();
        var serve = Task.Run(async () =>
        {
            var context = await listener.GetContextAsync();
            onTheWire = context.Request.Headers[HttpHeaders.QueryParamPrefix + "sort"];
            context.Response.StatusCode = 200;
            context.Response.Close();
        });
        await ctx.Start();

        var producer = ctx.GetEndpoint($"http://127.0.0.1:{probePort}/probe?method=GET").CreateProducer();
        await producer.Start();
        var exchange = new Exchange(new Message());
        exchange.In.Headers[HttpHeaders.QueryParamPrefix + "sort"] = "asc";
        await producer.Process(exchange);
        await producer.Stop();
        await serve.WaitAsync(TimeSpan.FromSeconds(10));

        onTheWire.Should().Be("asc", "the producer's list names only some redbHttp.* headers");
    }

    [Fact]
    public async Task ARedbHttpMethodHeaderOnTheExchange_SetsTheMethod()
    {
        // Finding 3.1: whatever put it there (a broker message's headers cross into the exchange whole).
        await Send(e => e.In.Headers[HttpHeaders.Method] = "DELETE");

        var request = await _received.Task.WaitAsync(TimeSpan.FromSeconds(10));
        request[HttpHeaders.Method].Should().Be("DELETE", "the endpoint said POST; the header won");
    }

    [Fact]
    public async Task TheResponse_DoesNotEchoTheRequestHeaders()
    {
        var producer = _ctx.GetEndpoint($"http://127.0.0.1:{_port}/echo?method=POST").CreateProducer();
        await producer.Start();
        var exchange = new Exchange(new Message("body"));
        exchange.In.Headers["X-Corr"] = "c1";
        await producer.Process(exchange);
        await producer.Stop();

        var response = (exchange.HasOut ? exchange.Out! : exchange.In).Headers;
        response["X-Reply"].Should().Be("r1");
        response.Should().NotContainKey("X-Corr", "the consumer does not answer with a value the request brought");
    }

    [Fact]
    public async Task SkipRequestHeaders_SendsNoExchangeHeader()
    {
        await Send(e => e.In.Headers["X-Corr"] = "c1", "method=POST&skipRequestHeaders=true");

        var request = await _received.Task.WaitAsync(TimeSpan.FromSeconds(10));
        request.Should().NotContainKey("X-Corr");
    }
}

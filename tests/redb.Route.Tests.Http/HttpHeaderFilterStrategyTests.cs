using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Http;

namespace redb.Route.Tests.Http;

/// <summary>
/// <c>headerFilterStrategy=#name</c> on HTTP endpoints, and the options around it: a strategy replaces the component's
/// default on every point of the endpoint; the protocol fields and, on a consumer, received <c>redbHttp.*</c> and
/// response-only fields stay out whatever the strategy says; <c>skipControlHeaders</c> keeps the exchange's control
/// headers from steering the request; the options renamed to Apache Camel's names explain themselves.
/// </summary>
[Collection("HttpServer")]
public sealed class HttpHeaderFilterStrategyTests : IAsyncLifetime
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
        _ctx.AddToRegistry("correlationOnly", new DefaultHeaderFilterStrategy { OutFilter = ["Correlation-ID"], FilterOnMatch = false });
        _ctx.AddToRegistry("everything", new DefaultHeaderFilterStrategy { AllowNullValues = true });
        _ctx.AddToRegistry("noServerHeader", new DefaultHeaderFilterStrategy { InFilter = ["X-Server"] });
        var port = _port;
        _ctx.AddRoutes(r => r.From($"http://127.0.0.1:{port}/plain?inOut=true&methods=GET,POST,DELETE").Process(e =>
        {
            _received.TrySetResult(new Dictionary<string, object?>(e.In.Headers, StringComparer.OrdinalIgnoreCase));
            e.In.Headers["X-Server"] = "s1";
            e.In.Headers["X-Reply"] = "r1";
            e.In.Body = "reply";
        }));
        _ctx.AddRoutes(r => r.From($"http://127.0.0.1:{port}/guarded?inOut=true&methods=POST&headerFilterStrategy=#everything")
            .Process(e =>
            {
                _received.TrySetResult(new Dictionary<string, object?>(e.In.Headers, StringComparer.OrdinalIgnoreCase));
                e.In.Headers["redbInternal"] = "with the default it stays home";
                e.In.Body = "reply";
            }));
        await _ctx.Start();
    }

    public async Task DisposeAsync()
    {
        await _ctx.DisposeAsync();
        await _servers.DisposeAsync();
    }

    private async Task<IExchange> Send(string path, string options, Action<IExchange> headers)
    {
        var producer = _ctx.GetEndpoint($"http://127.0.0.1:{_port}/{path}?{options}").CreateProducer();
        await producer.Start();
        var exchange = new Exchange(new Message("body"));
        headers(exchange);
        await producer.Process(exchange);
        await producer.Stop();
        return exchange;
    }

    private Task<Dictionary<string, object?>> Received() => _received.Task.WaitAsync(TimeSpan.FromSeconds(10));

    [Fact]
    public async Task AWhitelist_SendsOnlyTheNamedHeader()
    {
        await Send("plain", "method=POST&headerFilterStrategy=#correlationOnly", e =>
        {
            e.In.Headers["Correlation-ID"] = "c-42";
            e.In.Headers["X-Corr"] = "c1";
            e.In.Headers["Authorization"] = "Bearer secret";
        });

        var request = await Received();
        request["Correlation-ID"].Should().Be("c-42");
        request.Should().NotContainKey("X-Corr");
        request.Should().NotContainKey("Authorization", "the whitelist does not name it");
    }

    [Fact]
    public async Task AStrategy_ReplacesTheDefault_ButNotTheProtocolFields()
    {
        // 'everything' lets every header through, the control headers the default keeps back included.
        await Send("plain", "method=POST&headerFilterStrategy=#everything", e =>
        {
            e.In.Headers[HttpHeaders.Url] = "http://elsewhere/";
            e.In.Headers["Connection"] = "close-me";
            e.In.Headers["Host"] = "elsewhere.example.com";
        });

        var request = await Received();
        request.Should().NotContainKey("Connection", "a protocol field is the transport's");
        request[HttpHeaders.Url].Should().NotBe("http://elsewhere/",
            "it went out under the strategy, but the consumer sets its own redbHttp.Url and never takes a received one");
    }

    [Fact]
    public async Task OnAProducer_TheStrategyAlsoFiltersTheResponse()
    {
        var sent = await Send("plain", "method=POST&headerFilterStrategy=#noServerHeader", _ => { });

        var response = (sent.HasOut ? sent.Out! : sent.In).Headers;
        response["X-Reply"].Should().Be("r1");
        response.Should().NotContainKey("X-Server", "the incoming filter of the producer's strategy names it");
    }

    [Fact]
    public async Task OnAConsumer_AStrategyNeverLetsInAReceivedRedbHttpHeader()
    {
        // The guard against a caller posing as the transport (redbHttp.RemoteAddress drives rate limiting) holds
        // under a strategy that lets everything through.
        await Send("guarded", "method=POST&headerFilterStrategy=#everything", e =>
        {
            e.In.Headers[HttpHeaders.RemoteAddress] = "10.0.0.1";
            e.In.Headers["Set-Cookie"] = "planted=1";
            e.In.Headers["X-Corr"] = "c1";
        });

        var request = await Received();
        request["X-Corr"].Should().Be("c1");
        request[HttpHeaders.RemoteAddress].Should().Be("127.0.0.1", "the socket address, not the caller's header");
        request.Should().NotContainKey("Set-Cookie", "a response-only field never comes in from a request");
    }

    [Fact]
    public async Task OnAConsumer_TheStrategyDecidesTheResponseHeaders()
    {
        var sent = await Send("guarded", "method=POST", _ => { });

        var response = (sent.HasOut ? sent.Out! : sent.In).Headers;
        response["redbInternal"].Should().Be("with the default it stays home",
            "'everything' lets out what the default keeps back (redb*)");
    }

    [Fact]
    public async Task SkipControlHeaders_TheEndpointDecidesTheMethodAndQuery()
    {
        await Send("plain", "method=POST&skipControlHeaders=true", e =>
        {
            e.In.Headers[HttpHeaders.Method] = "DELETE";
            e.In.Headers[HttpHeaders.Query] = "injected=1";
        });

        var request = await Received();
        request[HttpHeaders.Method].Should().Be("POST", "the control header from the exchange is ignored");
        request.Should().NotContainKey(HttpHeaders.QueryParamPrefix + "injected");
    }

    [Fact]
    public async Task AStrategyTheRegistryDoesNotHold_FailsWhenTheProducerIsBuilt()
    {
        var act = () => _ctx.GetEndpoint($"http://127.0.0.1:{_port}/plain?method=POST&headerFilterStrategy=#nope").CreateProducer();

        act.Should().Throw<InvalidOperationException>().WithMessage("*'#nope'*");
    }

    [Theory]
    [InlineData("bridgeHeaders", "skipRequestHeaders")]
    [InlineData("copyResponseHeaders", "skipResponseHeaders")]
    public void ARenamedOption_NamesItsCamelName(string old, string renamed)
    {
        var act = () => _ctx.GetEndpoint($"http://127.0.0.1:{_port}/plain?method=POST&{old}=false");

        act.Should().Throw<ArgumentException>().WithMessage($"*'{old}'*'{renamed}'*");
    }

    [Fact]
    public void TheDsl_WritesTheCamelNames()
    {
        var uri = redb.Route.Http.Http.Post("http://example.com/x")
            .SkipRequestHeaders().SkipResponseHeaders().SkipControlHeaders().HeaderFilterStrategy("correlationOnly")
            .Build();

        uri.Should().Contain("skipRequestHeaders=true").And.Contain("skipResponseHeaders=true")
            .And.Contain("skipControlHeaders=true").And.Contain("headerFilterStrategy=correlationOnly");
    }
}

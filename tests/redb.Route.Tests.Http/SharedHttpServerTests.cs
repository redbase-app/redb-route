using System.Net;
using System.Text;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Http;

namespace redb.Route.Tests.Http;

/// <summary>
/// Tests for SharedHttpServerManager: multiple routes on the same port,
/// route template parameters, query parameters, port header, 404/405 handling.
/// </summary>
[Collection("HttpServer")]
public class SharedHttpServerTests : IAsyncLifetime
{
    private SharedHttpServerManager _serverManager = null!;
    private HttpClient _client = null!;
    private int _port;
    private readonly List<HttpConsumer> _consumers = [];
    private readonly List<IExchange> _capturedExchanges = [];

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
        foreach (var consumer in _consumers)
            await consumer.Stop();
        await _serverManager.DisposeAsync();
    }

    // ── Helpers ──

    private HttpConsumer CreateConsumer(
        string path = "/webhook",
        Dictionary<string, string>? extraParams = null,
        Func<IExchange, Task>? onProcess = null)
    {
        var component = new HttpComponent();
        component.ServerManager = _serverManager;

        var parameters = new Dictionary<string, string>
        {
            ["host"] = "127.0.0.1",
            ["port"] = _port.ToString()
        };
        if (extraParams is not null)
            foreach (var (key, value) in extraParams)
                parameters[key] = value;

        var uriPath = $"/127.0.0.1:{_port}{path}";
        var uri = new EndpointUri("http", uriPath, $"http:{uriPath}", parameters);
        var endpoint = (HttpEndpoint)component.CreateEndpoint(uri);

        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(async ci =>
            {
                var ex = ci.Arg<IExchange>();
                _capturedExchanges.Add(ex);
                if (onProcess is not null)
                    await onProcess(ex);
            });

        var consumer = new HttpConsumer(endpoint, processor, endpoint.EndpointOptions, _serverManager);
        _consumers.Add(consumer);
        return consumer;
    }

    // ── Multiple routes on same port ──

    [Fact]
    public async Task MultipleRoutes_SamePort_BothAccessible()
    {
        var consumer1 = CreateConsumer("/api/users");
        var consumer2 = CreateConsumer("/api/orders");

        await consumer1.Start();
        await consumer2.Start();

        var r1 = await _client.GetAsync($"http://127.0.0.1:{_port}/api/users");
        var r2 = await _client.GetAsync($"http://127.0.0.1:{_port}/api/orders");

        r1.StatusCode.Should().Be(HttpStatusCode.OK);
        r2.StatusCode.Should().Be(HttpStatusCode.OK);
        _capturedExchanges.Count.Should().Be(2);
    }

    [Fact]
    public async Task MultipleRoutes_SamePort_SharedServer()
    {
        var consumer1 = CreateConsumer("/route1");
        var consumer2 = CreateConsumer("/route2");

        await consumer1.Start();
        await consumer2.Start();

        _serverManager.ServerCount.Should().Be(1);
    }

    [Fact]
    public async Task UnregisterRoute_OtherRouteStillWorks()
    {
        var consumer1 = CreateConsumer("/stay");
        var consumer2 = CreateConsumer("/leave");

        await consumer1.Start();
        await consumer2.Start();

        await consumer2.Stop();
        _consumers.Remove(consumer2);

        var r1 = await _client.GetAsync($"http://127.0.0.1:{_port}/stay");
        r1.StatusCode.Should().Be(HttpStatusCode.OK);

        var r2 = await _client.GetAsync($"http://127.0.0.1:{_port}/leave");
        r2.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task UnknownRoute_Returns404()
    {
        var consumer = CreateConsumer("/known");
        await consumer.Start();

        var response = await _client.GetAsync($"http://127.0.0.1:{_port}/unknown");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task MethodFilter_WrongMethod_Returns405()
    {
        var consumer = CreateConsumer("/post-only", new Dictionary<string, string> { ["methods"] = "POST" });
        await consumer.Start();

        var response = await _client.GetAsync($"http://127.0.0.1:{_port}/post-only");

        response.StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed);
    }

    // ── Route template parameters ──

    [Fact]
    public async Task RouteParam_ExtractedToHeaders()
    {
        IExchange? captured = null;
        var consumer = CreateConsumer("/api/users/{id}", onProcess: ex =>
        {
            captured = ex;
            return Task.CompletedTask;
        });
        await consumer.Start();

        var response = await _client.GetAsync($"http://127.0.0.1:{_port}/api/users/42");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        captured.Should().NotBeNull();
        captured!.In.Headers[$"{HttpHeaders.RouteParamPrefix}id"].Should().Be("42");
    }

    [Fact]
    public async Task RouteParam_MultipleParams()
    {
        IExchange? captured = null;
        var consumer = CreateConsumer("/api/{controller}/{action}/{id}", onProcess: ex =>
        {
            captured = ex;
            return Task.CompletedTask;
        });
        await consumer.Start();

        var response = await _client.GetAsync($"http://127.0.0.1:{_port}/api/orders/details/17");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        captured.Should().NotBeNull();
        captured!.In.Headers[$"{HttpHeaders.RouteParamPrefix}controller"].Should().Be("orders");
        captured!.In.Headers[$"{HttpHeaders.RouteParamPrefix}action"].Should().Be("details");
        captured!.In.Headers[$"{HttpHeaders.RouteParamPrefix}id"].Should().Be("17");
    }

    // ── Query parameters ──

    [Fact]
    public async Task QueryParam_ExtractedToHeaders()
    {
        IExchange? captured = null;
        var consumer = CreateConsumer("/search", onProcess: ex =>
        {
            captured = ex;
            return Task.CompletedTask;
        });
        await consumer.Start();

        var response = await _client.GetAsync($"http://127.0.0.1:{_port}/search?q=hello&page=2");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        captured.Should().NotBeNull();
        captured!.In.Headers[$"{HttpHeaders.QueryParamPrefix}q"].Should().Be("hello");
        captured!.In.Headers[$"{HttpHeaders.QueryParamPrefix}page"].Should().Be("2");
    }

    [Fact]
    public async Task QueryParam_RawQueryStillAvailable()
    {
        IExchange? captured = null;
        var consumer = CreateConsumer("/q", onProcess: ex =>
        {
            captured = ex;
            return Task.CompletedTask;
        });
        await consumer.Start();

        await _client.GetAsync($"http://127.0.0.1:{_port}/q?a=1&b=2");

        captured.Should().NotBeNull();
        captured!.In.Headers[HttpHeaders.Query].Should().Be("a=1&b=2");
    }

    // ── Port header ──

    [Fact]
    public async Task PortHeader_SetCorrectly()
    {
        IExchange? captured = null;
        var consumer = CreateConsumer("/porttest", onProcess: ex =>
        {
            captured = ex;
            return Task.CompletedTask;
        });
        await consumer.Start();

        await _client.GetAsync($"http://127.0.0.1:{_port}/porttest");

        captured.Should().NotBeNull();
        captured!.In.Headers[HttpHeaders.Port].Should().Be(_port);
    }

    // ── Route params + query params combined ──

    [Fact]
    public async Task RouteAndQueryParams_BothAvailable()
    {
        IExchange? captured = null;
        var consumer = CreateConsumer("/api/items/{id}", onProcess: ex =>
        {
            captured = ex;
            return Task.CompletedTask;
        });
        await consumer.Start();

        await _client.GetAsync($"http://127.0.0.1:{_port}/api/items/99?format=json&verbose=true");

        captured.Should().NotBeNull();
        captured!.In.Headers[$"{HttpHeaders.RouteParamPrefix}id"].Should().Be("99");
        captured!.In.Headers[$"{HttpHeaders.QueryParamPrefix}format"].Should().Be("json");
        captured!.In.Headers[$"{HttpHeaders.QueryParamPrefix}verbose"].Should().Be("true");
    }

    // ── Multiple consumers with different methods on same path ──

    [Fact]
    public async Task SamePath_DifferentMethods_BothWork()
    {
        IExchange? getExchange = null;
        IExchange? postExchange = null;

        var getConsumer = CreateConsumer("/resource",
            new Dictionary<string, string> { ["methods"] = "GET" },
            ex => { getExchange = ex; return Task.CompletedTask; });
        var postConsumer = CreateConsumer("/resource",
            new Dictionary<string, string> { ["methods"] = "POST" },
            ex => { postExchange = ex; return Task.CompletedTask; });

        await getConsumer.Start();
        await postConsumer.Start();

        var r1 = await _client.GetAsync($"http://127.0.0.1:{_port}/resource");
        var r2 = await _client.PostAsync($"http://127.0.0.1:{_port}/resource",
            new StringContent("data", Encoding.UTF8, "text/plain"));

        r1.StatusCode.Should().Be(HttpStatusCode.OK);
        r2.StatusCode.Should().Be(HttpStatusCode.OK);
        getExchange.Should().NotBeNull();
        postExchange.Should().NotBeNull();
        ((string)getExchange!.In.Headers[HttpHeaders.Method]).Should().Be("GET");
        ((string)postExchange!.In.Headers[HttpHeaders.Method]).Should().Be("POST");
    }

    // ── Scheme consistency ──

    [Fact]
    public void MixedSchemes_SamePort_Throws()
    {
        _serverManager.RegisterRoute("127.0.0.1", _port, "/a", null, _ => Task.CompletedTask, ssl: false);

        var act = () => _serverManager.RegisterRoute("127.0.0.1", _port, "/b", null, _ => Task.CompletedTask, ssl: true);

        act.Should().Throw<InvalidOperationException>();
    }

    // ── InOut mode with SharedServer ──

    [Fact]
    public async Task InOut_WithSharedServer_ReturnsResponse()
    {
        var consumer = CreateConsumer("/echo",
            new Dictionary<string, string> { ["inOut"] = "true" },
            ex =>
            {
                ex.Out = new Message("echoed");
                return Task.CompletedTask;
            });
        await consumer.Start();

        var response = await _client.PostAsync($"http://127.0.0.1:{_port}/echo",
            new StringContent("ping", Encoding.UTF8, "text/plain"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Be("echoed");
    }

    // ── Listener-wide settings must agree across routes ──

    [Fact]
    public void Conflicting_protocol_on_one_port_is_rejected()
    {
        // One listener cannot speak two protocol sets. Keeping the first value silently used to put a
        // gRPC route (HTTP/2 only) on an HTTP/1.1 listener, where every call failed with an unreadable
        // framing error instead of a configuration one.
        var manager = new SharedHttpServerManager();
        manager.RegisterRoute("127.0.0.1", _port, "/a", "POST", _ => Task.CompletedTask);

        var act = () => manager.RegisterRoute("127.0.0.1", _port, "/b", "POST", _ => Task.CompletedTask,
            protocol: HttpProtocol.Http2);

        act.Should().Throw<InvalidOperationException>().WithMessage("*already listening as*");
    }

    [Fact]
    public void Conflicting_client_certificate_mode_on_one_port_is_rejected()
    {
        // The client-certificate policy belongs to the TLS handshake, so it is per listener, not per route.
        var manager = new SharedHttpServerManager();
        manager.RegisterRoute("127.0.0.1", _port, "/a", "POST", _ => Task.CompletedTask);

        var act = () => manager.RegisterRoute("127.0.0.1", _port, "/b", "POST", _ => Task.CompletedTask,
            clientCertificateMode: Microsoft.AspNetCore.Server.Kestrel.Https.ClientCertificateMode.RequireCertificate);

        act.Should().Throw<InvalidOperationException>().WithMessage("*client-certificate mode*");
    }

    // ── Util ──

    private static int GetFreePort()
    {
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}

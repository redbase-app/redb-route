using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Controllers;
using redb.Route.Controllers.Attributes;
using redb.Route.Http;

namespace redb.Route.Tests.Controllers;

/// <summary>
/// Integration tests: real HTTP request → HttpConsumer → HttpControllerDispatcher → Controller → HTTP response.
/// No mocked headers — the HTTP consumer sets redbHttp.Method, redbHttp.Path, etc. from the real HTTP request.
/// Uses raw HttpClient on the producer side for full realism.
/// </summary>
[Collection("HttpControllerIntegration")]
public class HttpControllerIntegrationTests : IAsyncLifetime
{
    private HttpConsumer? _consumer;
    private HttpClient? _httpClient;
    private int _port;
    private SharedHttpServerManager? _serverManager;
    private string _baseUrl = "";

    public Task InitializeAsync()
    {
        _port = GetFreePort();
        _serverManager = new SharedHttpServerManager();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _httpClient?.Dispose();
        if (_consumer is not null) await _consumer.Stop();
        if (_serverManager is not null) await _serverManager.DisposeAsync();
    }

    /// <summary>
    /// Creates a real HTTP consumer with HttpControllerDispatcher,
    /// listening on a catch-all route so any path reaches the dispatcher.
    /// </summary>
    private async Task StartConsumer(ControllerRegistry registry)
    {
        var context = new RouteContext();
        var dispatcher = new HttpControllerDispatcher(registry, context);

        var component = new HttpComponent();
        // Use {**path} catch-all so all incoming paths reach the dispatcher
        var consumerPath = $"/127.0.0.1:{_port}/{{**path}}";
        var consumerParams = new Dictionary<string, string>
        {
            ["host"] = "127.0.0.1",
            ["port"] = _port.ToString(),
            ["inOut"] = "true"
        };
        var consumerUri = new EndpointUri("http", consumerPath, $"http:{consumerPath}", consumerParams);
        var consumerEndpoint = (HttpEndpoint)component.CreateEndpoint(consumerUri);

        _consumer = new HttpConsumer(consumerEndpoint, dispatcher, consumerEndpoint.EndpointOptions, _serverManager!);
        await _consumer.Start();

        _baseUrl = $"http://127.0.0.1:{_port}";
        _httpClient = new HttpClient { BaseAddress = new Uri(_baseUrl) };
    }

    // ── GET — list items ────────────────────────────────

    [Fact]
    public async Task GET_modules_returns_list()
    {
        var registry = new ControllerRegistry();
        registry.RegisterController(typeof(ModulesController));
        await StartConsumer(registry);

        var response = await _httpClient!.GetAsync("/modules");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        var items = JsonSerializer.Deserialize<string[]>(body);
        items.Should().BeEquivalentTo(new[] { "module1", "module2" });
    }

    // ── GET with route param ────────────────────────────

    [Fact]
    public async Task GET_modules_by_id_returns_item()
    {
        var registry = new ControllerRegistry();
        registry.RegisterController(typeof(ModulesController));
        await StartConsumer(registry);

        var response = await _httpClient!.GetAsync("/modules/42");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("module-42");
    }

    // ── POST with JSON body ─────────────────────────────

    [Fact]
    public async Task POST_modules_creates_item()
    {
        var registry = new ControllerRegistry();
        registry.RegisterController(typeof(ModulesController));
        await StartConsumer(registry);

        var response = await _httpClient!.PostAsJsonAsync("/modules",
            new CreateModuleRequest { Name = "NewModule" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("NewModule");
    }

    // ── PUT with route param and body ───────────────────

    [Fact]
    public async Task PUT_modules_updates_item()
    {
        var registry = new ControllerRegistry();
        registry.RegisterController(typeof(ModulesController));
        await StartConsumer(registry);

        var response = await _httpClient!.PutAsJsonAsync("/modules/99",
            new CreateModuleRequest { Name = "Updated" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("Updated");
    }

    // ── DELETE ──────────────────────────────────────────

    [Fact]
    public async Task DELETE_modules_returns_success()
    {
        var registry = new ControllerRegistry();
        registry.RegisterController(typeof(ModulesController));
        await StartConsumer(registry);

        var response = await _httpClient!.DeleteAsync("/modules/77");

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    // ── Query params ────────────────────────────────────

    [Fact]
    public async Task GET_with_query_params()
    {
        var registry = new ControllerRegistry();
        registry.RegisterController(typeof(ContextsController));
        await StartConsumer(registry);

        var response = await _httpClient!.GetAsync("/contexts/myctx/status?verbose=true");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("myctx");
    }

    // ── 404 for unknown route ───────────────────────────

    [Fact]
    public async Task Unknown_route_returns_response_with_error()
    {
        var registry = new ControllerRegistry();
        registry.RegisterController(typeof(ModulesController));
        await StartConsumer(registry);

        var response = await _httpClient!.GetAsync("/nonexistent");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("NotFound");
    }

    // ── Multiple controllers ────────────────────────────

    [Fact]
    public async Task Multiple_controllers_dispatch_correctly()
    {
        var registry = new ControllerRegistry();
        registry.RegisterController(typeof(ModulesController));
        registry.RegisterController(typeof(ContextsController));
        await StartConsumer(registry);

        var modulesResponse = await _httpClient!.GetAsync("/modules");
        var contextsResponse = await _httpClient!.GetAsync("/contexts");

        modulesResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        contextsResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var modulesBody = await modulesResponse.Content.ReadAsStringAsync();
        var contextsBody = await contextsResponse.Content.ReadAsStringAsync();

        modulesBody.Should().Contain("module1");
        contextsBody.Should().Contain("ctx1");
    }

    private static int GetFreePort()
    {
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}

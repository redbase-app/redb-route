using System.Text;
using System.Text.Json;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Controllers;
using redb.Route.Controllers.Attributes;

namespace redb.Route.Tests.Controllers;

public class HttpControllerDispatcherTests
{
    /// <summary>Creates an exchange that mimics what HttpConsumer produces.</summary>
    private static IExchange CreateHttpExchange(
        string method, string path, object? body = null,
        Dictionary<string, string>? queryParams = null,
        Dictionary<string, object>? routeParams = null)
    {
        var exchange = new Exchange();

        // HTTP consumer stores body as byte[]
        if (body is byte[] bytes)
            exchange.In.Body = bytes;
        else if (body is not null)
            exchange.In.Body = JsonSerializer.SerializeToUtf8Bytes(body);

        exchange.In.setHeader("redbHttp.Method", method);
        exchange.In.setHeader("redbHttp.Path", path); // with leading /

        if (queryParams is not null)
        {
            foreach (var (key, value) in queryParams)
                exchange.In.setHeader($"redbHttp.QueryParam.{key}", value);
        }

        if (routeParams is not null)
        {
            foreach (var (key, value) in routeParams)
                exchange.In.setHeader($"redbHttp.RouteParam.{key}", value);
        }

        return exchange;
    }

    // ── Basic dispatch ──────────────────────────────────

    [Fact]
    public async Task Dispatches_GET_to_controller()
    {
        var registry = new ControllerRegistry();
        registry.RegisterController(typeof(ModulesController));

        var context = new RouteContext();
        var dispatcher = new HttpControllerDispatcher(registry, context);

        var exchange = CreateHttpExchange("GET", "/modules");

        await dispatcher.Process(exchange);

        exchange.Out.Should().NotBeNull();
        var json = System.Text.Encoding.UTF8.GetString((byte[])exchange.Out!.Body!);
        json.Should().Contain("module1").And.Contain("module2");
        exchange.Out.GetHeader<int>("status.code").Should().Be(200);
    }

    [Fact]
    public async Task Dispatches_GET_with_route_param()
    {
        var registry = new ControllerRegistry();
        registry.RegisterController(typeof(ModulesController));

        var context = new RouteContext();
        var dispatcher = new HttpControllerDispatcher(registry, context);

        // Path with template param — registry resolves {id} from path segments
        var exchange = CreateHttpExchange("GET", "/modules/42");

        await dispatcher.Process(exchange);

        exchange.Out.Should().NotBeNull();
        exchange.Out!.Body.Should().Be("module-42");
    }

    [Fact]
    public async Task Dispatches_POST_with_json_body()
    {
        var registry = new ControllerRegistry();
        registry.RegisterController(typeof(ModulesController));

        var context = new RouteContext();
        var dispatcher = new HttpControllerDispatcher(registry, context);

        // HTTP consumer serializes body as byte[]
        var exchange = CreateHttpExchange("POST", "/modules",
            new CreateModuleRequest { Name = "test-module" });

        await dispatcher.Process(exchange);

        exchange.Out.Should().NotBeNull();
        exchange.Out!.GetHeader<int>("status.code").Should().Be(200);
    }

    [Fact]
    public async Task Dispatches_DELETE_returns_204()
    {
        var registry = new ControllerRegistry();
        registry.RegisterController(typeof(ModulesController));

        var context = new RouteContext();
        var dispatcher = new HttpControllerDispatcher(registry, context);

        var exchange = CreateHttpExchange("DELETE", "/modules/42");

        await dispatcher.Process(exchange);

        exchange.Out.Should().NotBeNull();
        exchange.Out!.GetHeader<int>("status.code").Should().Be(204);
    }

    [Fact]
    public async Task Dispatches_PUT_with_route_and_body()
    {
        var registry = new ControllerRegistry();
        registry.RegisterController(typeof(ModulesController));

        var context = new RouteContext();
        var dispatcher = new HttpControllerDispatcher(registry, context);

        var exchange = CreateHttpExchange("PUT", "/modules/7",
            new CreateModuleRequest { Name = "updated" });

        await dispatcher.Process(exchange);

        exchange.Out.Should().NotBeNull();
        exchange.Out!.GetHeader<int>("status.code").Should().Be(200);
    }

    // ── Query parameters ────────────────────────────────

    [Fact]
    public async Task Resolves_query_parameter_from_http_headers()
    {
        var registry = new ControllerRegistry();
        registry.RegisterController(typeof(ContextsController));

        var context = new RouteContext();
        var dispatcher = new HttpControllerDispatcher(registry, context);

        var exchange = CreateHttpExchange("GET", "/contexts/myctx/status",
            queryParams: new Dictionary<string, string> { ["verbose"] = "true" });

        await dispatcher.Process(exchange);

        exchange.Out.Should().NotBeNull();
        exchange.Out!.GetHeader<int>("status.code").Should().Be(200);
    }

    // ── Multi-segment templates ─────────────────────────

    [Fact]
    public async Task Dispatches_multi_segment_with_param()
    {
        var registry = new ControllerRegistry();
        registry.RegisterController(typeof(ContextsController));

        var context = new RouteContext();
        var dispatcher = new HttpControllerDispatcher(registry, context);

        var exchange = CreateHttpExchange("POST", "/contexts/myctx/start");

        await dispatcher.Process(exchange);

        exchange.Out.Should().NotBeNull();
        exchange.Out!.GetHeader<int>("status.code").Should().Be(200);
    }

    // ── Error cases ─────────────────────────────────────

    [Fact]
    public async Task Returns_404_for_unknown_path()
    {
        var registry = new ControllerRegistry();
        registry.RegisterController(typeof(ModulesController));

        var context = new RouteContext();
        var dispatcher = new HttpControllerDispatcher(registry, context);

        var exchange = CreateHttpExchange("GET", "/nonexistent");

        await dispatcher.Process(exchange);

        exchange.Out.Should().NotBeNull();
        exchange.Out!.GetHeader<int>("status.code").Should().Be(404);
        exchange.Out!.Body.Should().BeOfType<byte[]>();
        var errorJson = System.Text.Encoding.UTF8.GetString((byte[])exchange.Out!.Body!);
        errorJson.Should().Contain("NotFound");
    }

    [Fact]
    public async Task Returns_400_for_missing_http_headers()
    {
        var registry = new ControllerRegistry();
        registry.RegisterController(typeof(ModulesController));

        var context = new RouteContext();
        var dispatcher = new HttpControllerDispatcher(registry, context);

        var exchange = new Exchange(); // no redbHttp.* headers at all

        await dispatcher.Process(exchange);

        exchange.Out.Should().NotBeNull();
        exchange.Out!.GetHeader<int>("status.code").Should().Be(400);
    }

    [Fact]
    public async Task Returns_404_for_wrong_method()
    {
        var registry = new ControllerRegistry();
        registry.RegisterController(typeof(ModulesController));

        var context = new RouteContext();
        var dispatcher = new HttpControllerDispatcher(registry, context);

        // PATCH /modules doesn't exist
        var exchange = CreateHttpExchange("PATCH", "/modules");

        await dispatcher.Process(exchange);

        exchange.Out.Should().NotBeNull();
        exchange.Out!.GetHeader<int>("status.code").Should().Be(404);
    }

    // ── Body deserialization ────────────────────────────

    [Fact]
    public async Task Deserializes_json_body_from_bytes()
    {
        var registry = new ControllerRegistry();
        registry.RegisterController(typeof(ModulesController));

        var context = new RouteContext();
        var dispatcher = new HttpControllerDispatcher(registry, context);

        // Simulate raw JSON bytes as HTTP consumer would provide
        var jsonBytes = Encoding.UTF8.GetBytes("""{"name":"from-bytes"}""");
        var exchange = CreateHttpExchange("POST", "/modules", jsonBytes);

        await dispatcher.Process(exchange);

        exchange.Out.Should().NotBeNull();
        exchange.Out!.GetHeader<int>("status.code").Should().Be(200);
    }

    // ── Controller convention (no [Route]) ──────────────

    [Fact]
    public async Task Dispatches_to_controller_without_route_attribute()
    {
        var registry = new ControllerRegistry();
        registry.RegisterController(typeof(NoRouteController));

        var context = new RouteContext();
        var dispatcher = new HttpControllerDispatcher(registry, context);

        var exchange = CreateHttpExchange("GET", "/noroute");

        await dispatcher.Process(exchange);

        exchange.Out.Should().NotBeNull();
        exchange.Out!.Body.Should().Be("ok");
    }

    // ── Path normalization ──────────────────────────────

    [Fact]
    public async Task Strips_leading_slash_from_path()
    {
        var registry = new ControllerRegistry();
        registry.RegisterController(typeof(ModulesController));

        var context = new RouteContext();
        var dispatcher = new HttpControllerDispatcher(registry, context);

        // Both /modules and modules should work
        var exchange1 = CreateHttpExchange("GET", "/modules");
        await dispatcher.Process(exchange1);
        exchange1.Out!.GetHeader<int>("status.code").Should().Be(200);

        var exchange2 = CreateHttpExchange("GET", "modules");
        await dispatcher.Process(exchange2);
        exchange2.Out!.GetHeader<int>("status.code").Should().Be(200);
    }

    // ── Dual-write: redbHttp.ResponseCode ───────────────

    [Fact]
    public async Task WriteResult_Sets_HttpResponseCode_200()
    {
        var registry = new ControllerRegistry();
        registry.RegisterController(typeof(ModulesController));
        var dispatcher = new HttpControllerDispatcher(registry, new RouteContext());

        var exchange = CreateHttpExchange("GET", "/modules");
        await dispatcher.Process(exchange);

        exchange.Out!.GetHeader<int>(HttpControllerDispatcher.HttpResponseCodeHeader).Should().Be(200);
    }

    [Fact]
    public async Task WriteResult_Null_Sets_HttpResponseCode_204()
    {
        var registry = new ControllerRegistry();
        registry.RegisterController(typeof(ModulesController));
        var dispatcher = new HttpControllerDispatcher(registry, new RouteContext());

        var exchange = CreateHttpExchange("DELETE", "/modules/1");
        await dispatcher.Process(exchange);

        exchange.Out!.GetHeader<int>(HttpControllerDispatcher.HttpResponseCodeHeader).Should().Be(204);
    }

    [Fact]
    public async Task WriteError_Sets_HttpResponseCode_404()
    {
        var registry = new ControllerRegistry();
        registry.RegisterController(typeof(ModulesController));
        var dispatcher = new HttpControllerDispatcher(registry, new RouteContext());

        var exchange = CreateHttpExchange("GET", "/nonexistent");
        await dispatcher.Process(exchange);

        exchange.Out!.GetHeader<int>(HttpControllerDispatcher.HttpResponseCodeHeader).Should().Be(404);
    }

    [Fact]
    public async Task WriteError_MissingHeaders_Sets_HttpResponseCode_400()
    {
        var registry = new ControllerRegistry();
        registry.RegisterController(typeof(ModulesController));
        var dispatcher = new HttpControllerDispatcher(registry, new RouteContext());

        var exchange = new Exchange(); // no method/path headers
        await dispatcher.Process(exchange);

        exchange.Out!.GetHeader<int>(HttpControllerDispatcher.HttpResponseCodeHeader).Should().Be(400);
    }
}

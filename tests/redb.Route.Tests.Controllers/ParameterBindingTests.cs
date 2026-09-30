using System.Globalization;
using System.Text;
using System.Text.Json;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Controllers;
using redb.Route.Controllers.Attributes;

namespace redb.Route.Tests.Controllers;

/// <summary>
/// Every binding source the attributes declare, checked by the VALUE the action received (it echoes it back),
/// on the generic dispatcher (<c>query.*</c> headers) and on the HTTP dispatcher (<c>redbHttp.QueryParam.*</c>).
/// A status-only check passes with a broken binding: an unbound <c>bool</c> is simply <c>false</c>.
/// </summary>
public class ParameterBindingTests
{
    private static ControllerRegistry Registry()
    {
        var registry = new ControllerRegistry();
        registry.RegisterController(typeof(BindingController));
        return registry;
    }

    private static async Task<object?> Generic(IExchange exchange, string method, string path)
    {
        exchange.In.setHeader(ControllerDispatcherProcessor.MethodHeader, method);
        exchange.In.setHeader(ControllerDispatcherProcessor.PathHeader, path);
        await using var context = new RouteContext();
        await new ControllerDispatcherProcessor(Registry(), context).Process(exchange);
        exchange.Out!.GetHeader<int>("status.code").Should().Be(200);
        return exchange.Out.Body;
    }

    private static async Task<string> Http(IExchange exchange, string method, string path,
        System.Text.Json.JsonSerializerOptions? jsonOptions = null)
    {
        exchange.In.setHeader(HttpControllerDispatcher.HttpMethodHeader, method);
        exchange.In.setHeader(HttpControllerDispatcher.HttpPathHeader, path);
        exchange.In.Body ??= Array.Empty<byte>();
        await using var context = new RouteContext();
        await new HttpControllerDispatcher(Registry(), context, jsonOptions).Process(exchange);
        exchange.Out!.GetHeader<int>("status.code").Should().Be(200);
        return exchange.Out.Body is byte[] bytes ? Encoding.UTF8.GetString(bytes) : exchange.Out.Body!.ToString()!;
    }

    // ── [FromHeader] ────────────────────────────────────

    [Fact]
    public async Task FromHeader_binds_the_named_header_and_converts_it()
    {
        IExchange exchange = new Exchange();
        exchange.In.setHeader("X-Tenant", "acme");
        exchange.In.setHeader("X-Limit", "25");

        (await Generic(exchange, "GET", "binding/header")).Should().Be("acme:25");
    }

    [Fact]
    public async Task FromHeader_missing_takes_the_parameter_default_or_the_type_default()
    {
        IExchange exchange = new Exchange();

        // tenant has no default (string → null), limit declares 10.
        (await Generic(exchange, "GET", "binding/header")).Should().Be(":10");
    }

    [Fact]
    public async Task FromHeader_binds_on_the_http_dispatcher_too()
    {
        IExchange exchange = new Exchange();
        exchange.In.setHeader("X-Tenant", "acme");

        (await Http(exchange, "GET", "/binding/header")).Should().Be("acme:10");
    }

    // ── [FromProperty] ──────────────────────────────────

    [Fact]
    public async Task FromProperty_binds_the_exchange_property_and_converts_it()
    {
        IExchange exchange = new Exchange();
        exchange.setProperty("user", "alice");
        exchange.setProperty("attempt", "3");

        (await Generic(exchange, "GET", "binding/property")).Should().Be("alice:3");
    }

    [Fact]
    public async Task FromProperty_missing_is_the_type_default()
    {
        (await Generic(new Exchange(), "GET", "binding/property")).Should().Be(":0");
    }

    [Fact]
    public async Task FromProperty_binds_on_the_http_dispatcher_too()
    {
        IExchange exchange = new Exchange();
        exchange.setProperty("user", "bob");
        exchange.setProperty("attempt", 7);

        (await Http(exchange, "GET", "/binding/property")).Should().Be("bob:7");
    }

    // ── [FromQuery] values ──────────────────────────────

    [Fact]
    public async Task FromQuery_binds_the_value_on_the_generic_dispatcher()
    {
        IExchange exchange = new Exchange();
        exchange.In.setHeader("query.verbose", "true");
        exchange.In.setHeader("query.page", "4");

        (await Generic(exchange, "GET", "binding/query")).Should().Be("True:4");
    }

    [Fact]
    public async Task FromQuery_binds_the_value_on_the_http_dispatcher()
    {
        IExchange exchange = new Exchange();
        exchange.In.setHeader($"{HttpControllerDispatcher.QueryParamPrefix}verbose", "true");

        (await Http(exchange, "GET", "/binding/query")).Should().Be("True:1");
    }

    // ── Simple types other than int/bool/Guid ───────────

    public static IEnumerable<object[]> Cultures() => [["en-US"], ["ru-RU"], ["de-DE"]];

    [Theory]
    [MemberData(nameof(Cultures))]
    public async Task Simple_types_bind_invariantly_whatever_the_thread_culture(string culture)
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
        try
        {
            var id = Guid.NewGuid();
            var exchange = new Exchange();
            exchange.In.setHeader($"{HttpControllerDispatcher.QueryParamPrefix}at", "2026-09-30T10:15:00");
            exchange.In.setHeader($"{HttpControllerDispatcher.QueryParamPrefix}offset", "2026-09-30T10:15:00+03:00");
            exchange.In.setHeader($"{HttpControllerDispatcher.QueryParamPrefix}amount", "12.5");
            exchange.In.setHeader($"{HttpControllerDispatcher.QueryParamPrefix}priority", "high");
            exchange.In.setHeader($"{HttpControllerDispatcher.QueryParamPrefix}id", id.ToString());

            var json = await Http(exchange, "GET", "/binding/types");
            var bound = JsonSerializer.Deserialize<BoundTypes>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

            using var scope = new FluentAssertions.Execution.AssertionScope();
            bound.At.Should().Be(new DateTime(2026, 9, 30, 10, 15, 0));
            bound.Offset.Should().Be(new DateTimeOffset(2026, 9, 30, 10, 15, 0, TimeSpan.FromHours(3)));
            bound.Amount.Should().Be(12.5m);
            bound.Priority.Should().Be(BindingPriority.High);
            bound.Id.Should().Be(id);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    // ── [HttpPatch] ─────────────────────────────────────

    [Fact]
    public async Task Patch_resolves_and_binds_route_and_body()
    {
        Registry().Resolve(HttpMethodType.Patch, "binding/9", out var routeParams)!
            .Method.Name.Should().Be(nameof(BindingController.Patch));
        routeParams["id"].Should().Be("9");

        IExchange exchange = new Exchange(new Message(JsonSerializer.SerializeToUtf8Bytes(new { name = "renamed" })));

        (await Http(exchange, "PATCH", "/binding/9")).Should().Be("patched 9:renamed");
    }

    // ── HttpControllerDispatcher(jsonOptions) ───────────

    [Fact]
    public async Task Custom_json_options_shape_both_the_request_and_the_reply()
    {
        var snake = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
        IExchange exchange = new Exchange(new Message(Encoding.UTF8.GetBytes("{\"module_name\":\"core\"}")));

        (await Http(exchange, "POST", "/binding/options", snake))
            .Should().Be("{\"module_name\":\"core\",\"is_created\":true}");
    }

    [Fact]
    public async Task Default_json_options_are_camel_case()
    {
        IExchange exchange = new Exchange(new Message(Encoding.UTF8.GetBytes("{\"moduleName\":\"core\"}")));

        (await Http(exchange, "POST", "/binding/options"))
            .Should().Be("{\"moduleName\":\"core\",\"isCreated\":true}");
    }
}

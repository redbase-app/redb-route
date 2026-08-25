using System.Text.Json;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Controllers;
using redb.Route.Controllers.Attributes;

namespace redb.Route.Tests.Controllers;

public class GrpcControllerDispatcherTests
{
    private static IExchange CreateExchange(string? method, object? body = null, bool serializeAsBytes = false)
    {
        var exchange = new Exchange();
        if (method is not null)
            exchange.In.setHeader(GrpcControllerDispatcher.MethodHeader, method);
        if (body is not null)
        {
            if (serializeAsBytes)
                exchange.In.Body = JsonSerializer.SerializeToUtf8Bytes(body);
            else
                exchange.In.Body = body;
        }
        return exchange;
    }

    // ── Single controller dispatch ──────────────────────

    [Fact]
    public async Task Dispatches_by_method_name()
    {
        var context = new RouteContext();
        var dispatcher = new GrpcControllerDispatcher(context, typeof(EchoController));

        var exchange = CreateExchange("Echo", "hello");

        await dispatcher.Process(exchange);

        exchange.Out.Should().NotBeNull();
        var body = DeserializeResult<string>(exchange.Out!.Body);
        body.Should().Be("echo:hello");
        exchange.Out.GetHeader<int>("status.code").Should().Be(200);
    }

    [Fact]
    public async Task Dispatches_no_args_method()
    {
        var context = new RouteContext();
        var dispatcher = new GrpcControllerDispatcher(context, typeof(EchoController));

        var exchange = CreateExchange("GetAll");

        await dispatcher.Process(exchange);

        exchange.Out.Should().NotBeNull();
        var body = DeserializeResult<string[]>(exchange.Out!.Body);
        body.Should().BeEquivalentTo(new[] { "item1", "item2" });
    }

    [Fact]
    public async Task Dispatches_with_byte_body()
    {
        var context = new RouteContext();
        var dispatcher = new GrpcControllerDispatcher(context, typeof(EchoController));

        // Simulate real gRPC body: byte[] containing JSON
        var exchange = CreateExchange("Echo", "hello", serializeAsBytes: true);

        await dispatcher.Process(exchange);

        exchange.Out.Should().NotBeNull();
        var body = DeserializeResult<string>(exchange.Out!.Body);
        body.Should().Be("echo:hello");
    }

    [Fact]
    public async Task Dispatches_complex_object_from_bytes()
    {
        var context = new RouteContext();
        var dispatcher = new GrpcControllerDispatcher(context, typeof(EchoController));

        var request = new CreateModuleRequest { Name = "TestModule" };
        var exchange = CreateExchange("Create", request, serializeAsBytes: true);

        await dispatcher.Process(exchange);

        exchange.Out.Should().NotBeNull();
        var result = DeserializeResult<JsonElement>(exchange.Out!.Body);
        result.GetProperty("Name").GetString().Should().Be("TestModule");
        result.GetProperty("Created").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Dispatches_multiple_positional_args_from_bytes()
    {
        var context = new RouteContext();
        var dispatcher = new GrpcControllerDispatcher(context, typeof(EchoController));

        var args = new object[] { 42, new CreateModuleRequest { Name = "Updated" } };
        var exchange = CreateExchange("Update", args, serializeAsBytes: true);

        await dispatcher.Process(exchange);

        exchange.Out.Should().NotBeNull();
        var result = DeserializeResult<JsonElement>(exchange.Out!.Body);
        result.GetProperty("Id").GetInt32().Should().Be(42);
        result.GetProperty("Name").GetString().Should().Be("Updated");
        result.GetProperty("Updated").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Dispatches_async_method()
    {
        var context = new RouteContext();
        var dispatcher = new GrpcControllerDispatcher(context, typeof(EchoController));

        var exchange = CreateExchange("AsyncMethod", "test");

        await dispatcher.Process(exchange);

        exchange.Out.Should().NotBeNull();
        var body = DeserializeResult<string>(exchange.Out!.Body);
        body.Should().Be("async:test");
    }

    [Fact]
    public async Task Dispatches_void_method_returns_204()
    {
        var context = new RouteContext();
        var dispatcher = new GrpcControllerDispatcher(context, typeof(EchoController));

        var exchange = CreateExchange("Delete", 1);

        await dispatcher.Process(exchange);

        exchange.Out.Should().NotBeNull();
        exchange.Out!.GetHeader<int>("status.code").Should().Be(204);
    }

    [Fact]
    public async Task Dispatches_with_default_param()
    {
        var context = new RouteContext();
        var dispatcher = new GrpcControllerDispatcher(context, typeof(EchoController));

        var exchange = CreateExchange("WithDefault", "hello");

        await dispatcher.Process(exchange);

        exchange.Out.Should().NotBeNull();
        var body = DeserializeResult<string>(exchange.Out!.Body);
        body.Should().Be("hello:5");
    }

    // ── Multi-controller dispatch ───────────────────────

    [Fact]
    public async Task Qualified_name_dispatches_to_correct_controller()
    {
        var context = new RouteContext();
        var dispatcher = new GrpcControllerDispatcher(context,
            typeof(EchoController), typeof(StatusController));

        var exchange = CreateExchange("Status.GetAll");

        await dispatcher.Process(exchange);

        exchange.Out.Should().NotBeNull();
        var body = DeserializeResult<string>(exchange.Out!.Body);
        body.Should().Be("status-ok");
    }

    [Fact]
    public async Task Unqualified_name_resolves_first_registered()
    {
        var context = new RouteContext();
        var dispatcher = new GrpcControllerDispatcher(context,
            typeof(EchoController), typeof(StatusController));

        // EchoController registered first, so its GetAll wins
        var exchange = CreateExchange("GetAll");

        await dispatcher.Process(exchange);

        exchange.Out.Should().NotBeNull();
        var body = DeserializeResult<string[]>(exchange.Out!.Body);
        body.Should().BeEquivalentTo(new[] { "item1", "item2" });
    }

    // ── Error cases ─────────────────────────────────────

    [Fact]
    public async Task Missing_header_returns_400()
    {
        var context = new RouteContext();
        var dispatcher = new GrpcControllerDispatcher(context, typeof(EchoController));

        var exchange = CreateExchange(null);

        await dispatcher.Process(exchange);

        exchange.Out.Should().NotBeNull();
        exchange.Out!.GetHeader<int>("status.code").Should().Be(400);
    }

    [Fact]
    public async Task Unknown_method_returns_404()
    {
        var context = new RouteContext();
        var dispatcher = new GrpcControllerDispatcher(context, typeof(EchoController));

        var exchange = CreateExchange("NonExistent");

        await dispatcher.Process(exchange);

        exchange.Out.Should().NotBeNull();
        exchange.Out!.GetHeader<int>("status.code").Should().Be(404);
    }

    [Fact]
    public async Task Case_insensitive_dispatch()
    {
        var context = new RouteContext();
        var dispatcher = new GrpcControllerDispatcher(context, typeof(EchoController));

        var exchange = CreateExchange("echo", "test");

        await dispatcher.Process(exchange);

        exchange.Out.Should().NotBeNull();
        var body = DeserializeResult<string>(exchange.Out!.Body);
        body.Should().Be("echo:test");
    }

    [Fact]
    public async Task Empty_byte_array_body_treated_as_null()
    {
        var context = new RouteContext();
        var dispatcher = new GrpcControllerDispatcher(context, typeof(EchoController));

        var exchange = CreateExchange("GetAll");
        exchange.In.Body = Array.Empty<byte>();

        await dispatcher.Process(exchange);

        exchange.Out.Should().NotBeNull();
        var body = DeserializeResult<string[]>(exchange.Out!.Body);
        body.Should().BeEquivalentTo(new[] { "item1", "item2" });
    }

    [Fact]
    public void Ctor_throws_for_non_controller_type()
    {
        var context = new RouteContext();
        var act = () => new GrpcControllerDispatcher(context, typeof(string));
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Ctor_throws_for_empty_types()
    {
        var context = new RouteContext();
        var act = () => new GrpcControllerDispatcher(context);
        act.Should().Throw<ArgumentException>();
    }

    // ── JSON-object body binds BY NAME (arrays/null stay positional) ─────

    [Fact]
    public async Task JsonObject_FromBody_param_gets_whole_object()
    {
        var ex = CreateExchange("CreateBody", new CreateModuleRequest { Name = "N" }, serializeAsBytes: true);
        await new GrpcControllerDispatcher(new RouteContext(), typeof(BindController)).Process(ex);
        DeserializeResult<string>(ex.Out!.Body).Should().Be("N");
    }

    [Fact]
    public async Task JsonObject_lone_complex_param_gets_whole_object()   // preserves today's behavior
    {
        var ex = CreateExchange("CreateBare", new CreateModuleRequest { Name = "N" }, serializeAsBytes: true);
        await new GrpcControllerDispatcher(new RouteContext(), typeof(BindController)).Process(ex);
        DeserializeResult<string>(ex.Out!.Body).Should().Be("N");
    }

    [Fact]
    public async Task JsonObject_binds_route_and_body_by_name()   // was broken: object became id, body stayed null
    {
        var ex = CreateExchange("Update");
        ex.In.Body = JsonSerializer.SerializeToUtf8Bytes(new { id = "X", name = "N" });
        await new GrpcControllerDispatcher(new RouteContext(), typeof(BindController)).Process(ex);
        DeserializeResult<string>(ex.Out!.Body).Should().Be("X|N");
    }

    [Fact]
    public async Task JsonObject_binds_simple_by_name_and_keeps_defaults()   // was broken: object couldn't become int
    {
        var ex = CreateExchange("List");
        ex.In.Body = JsonSerializer.SerializeToUtf8Bytes(new { offset = 10 });
        await new GrpcControllerDispatcher(new RouteContext(), typeof(BindController)).Process(ex);
        DeserializeResult<string>(ex.Out!.Body).Should().Be("10:25");
    }

    [Fact]
    public async Task JsonArray_body_stays_positional()
    {
        var ex = CreateExchange("Update", new object[] { "X", new CreateModuleRequest { Name = "N" } }, serializeAsBytes: true);
        await new GrpcControllerDispatcher(new RouteContext(), typeof(BindController)).Process(ex);
        DeserializeResult<string>(ex.Out!.Body).Should().Be("X|N");
    }

    [Fact]
    public async Task JsonObject_extra_keys_do_not_break_binding()
    {
        var ex = CreateExchange("CreateBare");
        ex.In.Body = JsonSerializer.SerializeToUtf8Bytes(new { name = "N", unexpected = "ignored" });
        await new GrpcControllerDispatcher(new RouteContext(), typeof(BindController)).Process(ex);
        DeserializeResult<string>(ex.Out!.Body).Should().Be("N");
    }

    // ── Helper ──────────────────────────────────────────

    /// <summary>
    /// GrpcControllerDispatcher serializes results to byte[] (UTF-8 JSON).
    /// This helper deserializes them back for assertions.
    /// </summary>
    private static T DeserializeResult<T>(object? body)
    {
        if (body is byte[] bytes)
            return JsonSerializer.Deserialize<T>(bytes, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        if (body is T typed)
            return typed;
        throw new InvalidOperationException($"Expected byte[] or {typeof(T).Name}, got {body?.GetType().Name ?? "null"}");
    }
}

/// <summary>Controller exercising the JSON-object name-binding rules for <c>ResolvePositional</c>.</summary>
public class BindController : RedbController
{
    public string CreateBody([FromBody] CreateModuleRequest r) => r.Name;          // [FromBody] → whole object
    public string CreateBare(CreateModuleRequest request) => request.Name;         // lone complex → whole object
    public string Update([FromRoute("id")] string id, [FromBody] CreateModuleRequest r) => $"{id}|{r.Name}";
    public string List(int offset = 0, int count = 25) => $"{offset}:{count}";     // simple params by key, defaults kept
}

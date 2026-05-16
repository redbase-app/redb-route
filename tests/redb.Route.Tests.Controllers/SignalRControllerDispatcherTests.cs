using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Controllers;

namespace redb.Route.Tests.Controllers;

#region Test Controllers for SignalR

/// <summary>Controller with methods callable via SignalR hub invocations.</summary>
public class EchoController : RedbController
{
    public string Echo(string message) => $"echo:{message}";

    public string[] GetAll() => ["item1", "item2"];

    public object GetById(int id) => new { Id = id, Name = $"item-{id}" };

    public object Create(CreateModuleRequest request) =>
        new { Name = request.Name, Created = true };

    public object Update(int id, CreateModuleRequest request) =>
        new { Id = id, Name = request.Name, Updated = true };

    public void Delete(int id) { }

    public async Task<string> AsyncMethod(string input)
    {
        await Task.Yield();
        return $"async:{input}";
    }

    public string WithDefault(string value, int count = 5) => $"{value}:{count}";

    public string WithCancellation(string value, CancellationToken ct) => $"ok:{value}";
}

/// <summary>Second controller for multi-controller dispatch tests.</summary>
public class StatusController : RedbController
{
    public string GetAll() => "status-ok";

    public object Health() => new { Status = "healthy", Uptime = 12345 };
}

#endregion

public class SignalRControllerDispatcherTests
{
    private static IExchange CreateExchange(string? method, object? body = null)
    {
        var exchange = new Exchange();
        if (method is not null)
            exchange.In.setHeader("redbSignalR.Method", method);
        if (body is not null)
            exchange.In.Body = body;
        return exchange;
    }

    // ── Single controller dispatch ──────────────────────

    [Fact]
    public async Task Dispatches_by_method_name()
    {
        var context = new RouteContext();
        var dispatcher = new SignalRControllerDispatcher(context, typeof(EchoController));

        var exchange = CreateExchange("Echo", "hello");

        await dispatcher.Process(exchange);

        exchange.Out.Should().NotBeNull();
        exchange.Out!.Body.Should().Be("echo:hello");
        exchange.Out.GetHeader<int>("status.code").Should().Be(200);
    }

    [Fact]
    public async Task Dispatches_no_args_method()
    {
        var context = new RouteContext();
        var dispatcher = new SignalRControllerDispatcher(context, typeof(EchoController));

        var exchange = CreateExchange("GetAll");

        await dispatcher.Process(exchange);

        exchange.Out.Should().NotBeNull();
        exchange.Out!.Body.Should().BeEquivalentTo(new[] { "item1", "item2" });
    }

    [Fact]
    public async Task Dispatches_single_primitive_arg()
    {
        var context = new RouteContext();
        var dispatcher = new SignalRControllerDispatcher(context, typeof(EchoController));

        var exchange = CreateExchange("GetById", 42);

        await dispatcher.Process(exchange);

        exchange.Out.Should().NotBeNull();
        exchange.Out!.Body.Should().BeEquivalentTo(new { Id = 42, Name = "item-42" });
    }

    [Fact]
    public async Task Dispatches_complex_body_arg()
    {
        var context = new RouteContext();
        var dispatcher = new SignalRControllerDispatcher(context, typeof(EchoController));

        var exchange = CreateExchange("Create", new CreateModuleRequest { Name = "test" });

        await dispatcher.Process(exchange);

        exchange.Out.Should().NotBeNull();
        exchange.Out!.Body.Should().BeEquivalentTo(new { Name = "test", Created = true });
    }

    [Fact]
    public async Task Dispatches_multiple_positional_args()
    {
        var context = new RouteContext();
        var dispatcher = new SignalRControllerDispatcher(context, typeof(EchoController));

        // Multiple args come as object[] from RedbBridgeHub
        var exchange = CreateExchange("Update",
            new object[] { 7, new CreateModuleRequest { Name = "updated" } });

        await dispatcher.Process(exchange);

        exchange.Out.Should().NotBeNull();
        exchange.Out!.Body.Should().BeEquivalentTo(new { Id = 7, Name = "updated", Updated = true });
    }

    [Fact]
    public async Task Dispatches_void_method_returns_204()
    {
        var context = new RouteContext();
        var dispatcher = new SignalRControllerDispatcher(context, typeof(EchoController));

        var exchange = CreateExchange("Delete", 42);

        await dispatcher.Process(exchange);

        exchange.Out.Should().NotBeNull();
        exchange.Out!.GetHeader<int>("status.code").Should().Be(204);
    }

    [Fact]
    public async Task Dispatches_async_method()
    {
        var context = new RouteContext();
        var dispatcher = new SignalRControllerDispatcher(context, typeof(EchoController));

        var exchange = CreateExchange("AsyncMethod", "test");

        await dispatcher.Process(exchange);

        exchange.Out.Should().NotBeNull();
        exchange.Out!.Body.Should().Be("async:test");
    }

    [Fact]
    public async Task Resolves_default_parameter_when_not_provided()
    {
        var context = new RouteContext();
        var dispatcher = new SignalRControllerDispatcher(context, typeof(EchoController));

        // Only provide first arg — second should use default value (5)
        var exchange = CreateExchange("WithDefault", "hello");

        await dispatcher.Process(exchange);

        exchange.Out.Should().NotBeNull();
        exchange.Out!.Body.Should().Be("hello:5");
    }

    [Fact]
    public async Task Skips_CancellationToken_in_positional_binding()
    {
        var context = new RouteContext();
        var dispatcher = new SignalRControllerDispatcher(context, typeof(EchoController));

        // CancellationToken is not the caller's concern — only "value" is provided
        var exchange = CreateExchange("WithCancellation", "test");

        await dispatcher.Process(exchange);

        exchange.Out.Should().NotBeNull();
        exchange.Out!.Body.Should().Be("ok:test");
    }

    [Fact]
    public async Task Method_name_is_case_insensitive()
    {
        var context = new RouteContext();
        var dispatcher = new SignalRControllerDispatcher(context, typeof(EchoController));

        var exchange = CreateExchange("getall");

        await dispatcher.Process(exchange);

        exchange.Out.Should().NotBeNull();
        exchange.Out!.Body.Should().BeEquivalentTo(new[] { "item1", "item2" });
    }

    // ── Error cases ─────────────────────────────────────

    [Fact]
    public async Task Returns_400_when_method_header_missing()
    {
        var context = new RouteContext();
        var dispatcher = new SignalRControllerDispatcher(context, typeof(EchoController));

        var exchange = CreateExchange(null); // no redbSignalR.Method header

        await dispatcher.Process(exchange);

        exchange.Out.Should().NotBeNull();
        exchange.Out!.GetHeader<int>("status.code").Should().Be(400);
        exchange.Out!.Body.Should().BeOfType<ControllerErrorResponse>();
    }

    [Fact]
    public async Task Returns_404_when_method_not_found()
    {
        var context = new RouteContext();
        var dispatcher = new SignalRControllerDispatcher(context, typeof(EchoController));

        var exchange = CreateExchange("NonExistentMethod");

        await dispatcher.Process(exchange);

        exchange.Out.Should().NotBeNull();
        exchange.Out!.GetHeader<int>("status.code").Should().Be(404);
        exchange.Out!.Body.Should().BeOfType<ControllerErrorResponse>();
    }

    [Fact]
    public void Rejects_non_controller_type()
    {
        var context = new RouteContext();
        var act = () => new SignalRControllerDispatcher(context, typeof(string));

        act.Should().Throw<ArgumentException>()
            .WithMessage("*does not inherit from RedbController*");
    }

    [Fact]
    public void Requires_at_least_one_controller()
    {
        var context = new RouteContext();
        var act = () => new SignalRControllerDispatcher(context);

        act.Should().Throw<ArgumentException>()
            .WithMessage("*At least one controller*");
    }

    // ── Multi-controller dispatch ───────────────────────

    [Fact]
    public async Task Multi_qualified_name_dispatches_correctly()
    {
        var context = new RouteContext();
        var dispatcher = new SignalRControllerDispatcher(context,
            typeof(EchoController), typeof(StatusController));

        // Qualified: "Status.Health"
        var exchange = CreateExchange("Status.Health");

        await dispatcher.Process(exchange);

        exchange.Out.Should().NotBeNull();
        exchange.Out!.Body.Should().BeEquivalentTo(new { Status = "healthy", Uptime = 12345 });
    }

    [Fact]
    public async Task Multi_unqualified_unique_method_resolves()
    {
        var context = new RouteContext();
        var dispatcher = new SignalRControllerDispatcher(context,
            typeof(EchoController), typeof(StatusController));

        // "Health" is unique to StatusController
        var exchange = CreateExchange("Health");

        await dispatcher.Process(exchange);

        exchange.Out.Should().NotBeNull();
        exchange.Out!.Body.Should().BeEquivalentTo(new { Status = "healthy", Uptime = 12345 });
    }

    [Fact]
    public async Task Multi_ambiguous_unqualified_resolves_first_registered()
    {
        var context = new RouteContext();
        var dispatcher = new SignalRControllerDispatcher(context,
            typeof(EchoController), typeof(StatusController));

        // "GetAll" exists on both — resolves to EchoController (registered first, TryAdd)
        var exchange = CreateExchange("GetAll");

        await dispatcher.Process(exchange);

        exchange.Out.Should().NotBeNull();
        // EchoController.GetAll returns string[] {"item1","item2"}
        exchange.Out!.Body.Should().BeEquivalentTo(new[] { "item1", "item2" });
    }

    [Fact]
    public async Task Multi_qualified_disambiguates_collision()
    {
        var context = new RouteContext();
        var dispatcher = new SignalRControllerDispatcher(context,
            typeof(EchoController), typeof(StatusController));

        // "Status.GetAll" resolves to StatusController despite collision
        var exchange = CreateExchange("Status.GetAll");

        await dispatcher.Process(exchange);

        exchange.Out.Should().NotBeNull();
        exchange.Out!.Body.Should().Be("status-ok");
    }

    // ── Controller context injection ────────────────────

    [Fact]
    public async Task Controller_has_context_and_exchange_injected()
    {
        var context = new RouteContext();
        var dispatcher = new SignalRControllerDispatcher(context, typeof(ContextCheckController));

        var exchange = CreateExchange("Check");

        await dispatcher.Process(exchange);

        exchange.Out.Should().NotBeNull();
        exchange.Out!.Body.Should().Be("ok");
    }
}

/// <summary>Controller that verifies Context and Exchange are injected.</summary>
public class ContextCheckController : RedbController
{
    public string Check()
    {
        if (Context is null) throw new InvalidOperationException("Context is null");
        if (Exchange is null) throw new InvalidOperationException("Exchange is null");
        return "ok";
    }
}

public class PositionalParameterResolverTests
{
    private static System.Reflection.MethodInfo GetMethod<T>(string name)
        => typeof(T).GetMethod(name, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)!;

    [Fact]
    public void Resolves_no_params_with_null_body()
    {
        var method = GetMethod<EchoController>("GetAll");
        var result = ParameterResolver.ResolvePositional(method, null);

        result.Should().BeEmpty();
    }

    [Fact]
    public void Resolves_single_string_arg()
    {
        var method = GetMethod<EchoController>("Echo");
        var result = ParameterResolver.ResolvePositional(method, "hello");

        result.Should().HaveCount(1);
        result[0].Should().Be("hello");
    }

    [Fact]
    public void Resolves_single_int_arg_with_type_conversion()
    {
        var method = GetMethod<EchoController>("GetById");
        // SignalR may send int as long (JSON protocol)
        var result = ParameterResolver.ResolvePositional(method, 42L);

        result.Should().HaveCount(1);
        result[0].Should().Be(42);
    }

    [Fact]
    public void Resolves_multiple_positional_args()
    {
        var method = GetMethod<EchoController>("Update");
        var body = new object[] { 7, new CreateModuleRequest { Name = "test" } };

        var result = ParameterResolver.ResolvePositional(method, body);

        result.Should().HaveCount(2);
        result[0].Should().Be(7);
        result[1].Should().BeOfType<CreateModuleRequest>();
    }

    [Fact]
    public void Uses_default_value_for_missing_args()
    {
        var method = GetMethod<EchoController>("WithDefault");
        var result = ParameterResolver.ResolvePositional(method, "hello");

        result.Should().HaveCount(2);
        result[0].Should().Be("hello");
        result[1].Should().Be(5); // default value
    }

    [Fact]
    public void Injects_CancellationToken()
    {
        var method = GetMethod<EchoController>("WithCancellation");
        using var cts = new CancellationTokenSource();
        var result = ParameterResolver.ResolvePositional(method, "test", cts.Token);

        result.Should().HaveCount(2);
        result[0].Should().Be("test");
        result[1].Should().Be(cts.Token);
    }

    [Fact]
    public void Handles_all_null_args_for_reference_params()
    {
        var method = GetMethod<EchoController>("Echo");
        var result = ParameterResolver.ResolvePositional(method, null);

        result.Should().HaveCount(1);
        result[0].Should().BeNull(); // string param, no value
    }
}

using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Controllers;

namespace redb.Route.Tests.Controllers;

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
        await using var context = new RouteContext();
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
        await using var context = new RouteContext();
        var dispatcher = new SignalRControllerDispatcher(context, typeof(EchoController));

        var exchange = CreateExchange("GetAll");

        await dispatcher.Process(exchange);

        exchange.Out.Should().NotBeNull();
        exchange.Out!.Body.Should().BeEquivalentTo(new[] { "item1", "item2" });
    }

    [Fact]
    public async Task Dispatches_single_primitive_arg()
    {
        await using var context = new RouteContext();
        var dispatcher = new SignalRControllerDispatcher(context, typeof(EchoController));

        var exchange = CreateExchange("GetById", 42);

        await dispatcher.Process(exchange);

        exchange.Out.Should().NotBeNull();
        exchange.Out!.Body.Should().BeEquivalentTo(new { Id = 42, Name = "item-42" });
    }

    [Fact]
    public async Task Dispatches_complex_body_arg()
    {
        await using var context = new RouteContext();
        var dispatcher = new SignalRControllerDispatcher(context, typeof(EchoController));

        var exchange = CreateExchange("Create", new CreateModuleRequest { Name = "test" });

        await dispatcher.Process(exchange);

        exchange.Out.Should().NotBeNull();
        exchange.Out!.Body.Should().BeEquivalentTo(new { Name = "test", Created = true });
    }

    [Fact]
    public async Task Dispatches_multiple_positional_args()
    {
        await using var context = new RouteContext();
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
        await using var context = new RouteContext();
        var dispatcher = new SignalRControllerDispatcher(context, typeof(EchoController));

        var exchange = CreateExchange("Delete", 42);

        await dispatcher.Process(exchange);

        exchange.Out.Should().NotBeNull();
        exchange.Out!.GetHeader<int>("status.code").Should().Be(204);
    }

    [Fact]
    public async Task Dispatches_async_method()
    {
        await using var context = new RouteContext();
        var dispatcher = new SignalRControllerDispatcher(context, typeof(EchoController));

        var exchange = CreateExchange("AsyncMethod", "test");

        await dispatcher.Process(exchange);

        exchange.Out.Should().NotBeNull();
        exchange.Out!.Body.Should().Be("async:test");
    }

    [Fact]
    public async Task Resolves_default_parameter_when_not_provided()
    {
        await using var context = new RouteContext();
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
        await using var context = new RouteContext();
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
        await using var context = new RouteContext();
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
        await using var context = new RouteContext();
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
        await using var context = new RouteContext();
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
        using var context = new RouteContext();
        var act = () => new SignalRControllerDispatcher(context, typeof(string));

        act.Should().Throw<ArgumentException>()
            .WithMessage("*does not inherit from RedbController*");
    }

    [Fact]
    public void Requires_at_least_one_controller()
    {
        using var context = new RouteContext();
        var act = () => new SignalRControllerDispatcher(context);

        act.Should().Throw<ArgumentException>()
            .WithMessage("*At least one controller*");
    }

    // ── Multi-controller dispatch ───────────────────────

    [Fact]
    public async Task Multi_qualified_name_dispatches_correctly()
    {
        await using var context = new RouteContext();
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
        await using var context = new RouteContext();
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
        await using var context = new RouteContext();
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
        await using var context = new RouteContext();
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
        await using var context = new RouteContext();
        var dispatcher = new SignalRControllerDispatcher(context, typeof(ContextCheckController));

        var exchange = CreateExchange("Check");

        await dispatcher.Process(exchange);

        exchange.Out.Should().NotBeNull();
        exchange.Out!.Body.Should().Be("ok");
    }
}

using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Controllers;
using redb.Route.Controllers.Attributes;

namespace redb.Route.Tests.Controllers;

public class ControllerDispatcherProcessorTests
{
    [Fact]
    public async Task Dispatches_GET_to_controller()
    {
        var registry = new ControllerRegistry();
        registry.RegisterController(typeof(ModulesController));

        await using var context = new RouteContext();
        var dispatcher = new ControllerDispatcherProcessor(registry, context);

        var exchange = new Exchange();
        exchange.In.setHeader(ControllerDispatcherProcessor.PathHeader, "modules");
        exchange.In.setHeader(ControllerDispatcherProcessor.MethodHeader, "GET");

        await dispatcher.Process(exchange);

        exchange.Out.Should().NotBeNull();
        exchange.Out!.Body.Should().BeEquivalentTo(new[] { "module1", "module2" });
        exchange.Out.GetHeader<int>("status.code").Should().Be(200);
    }

    [Fact]
    public async Task Dispatches_GET_with_route_param()
    {
        var registry = new ControllerRegistry();
        registry.RegisterController(typeof(ModulesController));

        await using var context = new RouteContext();
        var dispatcher = new ControllerDispatcherProcessor(registry, context);

        var exchange = new Exchange();
        exchange.In.setHeader(ControllerDispatcherProcessor.PathHeader, "modules/42");
        exchange.In.setHeader(ControllerDispatcherProcessor.MethodHeader, "GET");

        await dispatcher.Process(exchange);

        exchange.Out.Should().NotBeNull();
        exchange.Out!.Body.Should().Be("module-42");
    }

    [Fact]
    public async Task Dispatches_POST_with_body()
    {
        var registry = new ControllerRegistry();
        registry.RegisterController(typeof(ModulesController));

        await using var context = new RouteContext();
        var dispatcher = new ControllerDispatcherProcessor(registry, context);

        var exchange = new Exchange();
        exchange.In.setHeader(ControllerDispatcherProcessor.PathHeader, "modules");
        exchange.In.setHeader(ControllerDispatcherProcessor.MethodHeader, "POST");
        exchange.In.Body = new CreateModuleRequest { Name = "test-module" };

        await dispatcher.Process(exchange);

        exchange.Out.Should().NotBeNull();
        exchange.Out!.GetHeader<int>("status.code").Should().Be(200);
        System.Text.Json.JsonSerializer.Serialize(exchange.Out.Body).Should().Be("{\"Name\":\"test-module\",\"Created\":true}");
    }

    [Fact]
    public async Task Returns_404_for_unknown_path()
    {
        var registry = new ControllerRegistry();
        registry.RegisterController(typeof(ModulesController));

        await using var context = new RouteContext();
        var dispatcher = new ControllerDispatcherProcessor(registry, context);

        var exchange = new Exchange();
        exchange.In.setHeader(ControllerDispatcherProcessor.PathHeader, "unknown");
        exchange.In.setHeader(ControllerDispatcherProcessor.MethodHeader, "GET");

        await dispatcher.Process(exchange);

        exchange.Out.Should().NotBeNull();
        exchange.Out!.GetHeader<int>("status.code").Should().Be(404);
        exchange.Out!.Body.Should().BeOfType<ControllerErrorResponse>();
    }

    [Fact]
    public async Task Returns_400_for_missing_headers()
    {
        var registry = new ControllerRegistry();
        registry.RegisterController(typeof(ModulesController));

        await using var context = new RouteContext();
        var dispatcher = new ControllerDispatcherProcessor(registry, context);

        var exchange = new Exchange();

        await dispatcher.Process(exchange);

        exchange.Out.Should().NotBeNull();
        exchange.Out!.GetHeader<int>("status.code").Should().Be(400);
    }

    [Fact]
    public async Task DELETE_returns_204()
    {
        var registry = new ControllerRegistry();
        registry.RegisterController(typeof(ModulesController));

        await using var context = new RouteContext();
        var dispatcher = new ControllerDispatcherProcessor(registry, context);

        var exchange = new Exchange();
        exchange.In.setHeader(ControllerDispatcherProcessor.PathHeader, "modules/42");
        exchange.In.setHeader(ControllerDispatcherProcessor.MethodHeader, "DELETE");

        await dispatcher.Process(exchange);

        exchange.Out.Should().NotBeNull();
        exchange.Out!.GetHeader<int>("status.code").Should().Be(204);
    }

    [Fact]
    public async Task Resolves_query_parameter()
    {
        var registry = new ControllerRegistry();
        registry.RegisterController(typeof(ContextsController));

        await using var context = new RouteContext();
        var dispatcher = new ControllerDispatcherProcessor(registry, context);

        var exchange = new Exchange();
        exchange.In.setHeader(ControllerDispatcherProcessor.PathHeader, "contexts/myctx/status");
        exchange.In.setHeader(ControllerDispatcherProcessor.MethodHeader, "GET");
        exchange.In.setHeader("query.verbose", "true");

        await dispatcher.Process(exchange);

        exchange.Out.Should().NotBeNull();
        exchange.Out!.GetHeader<int>("status.code").Should().Be(200);
        // The value, not only the status: an unbound bool is false and still answers 200.
        System.Text.Json.JsonSerializer.Serialize(exchange.Out.Body).Should().Be("{\"Name\":\"myctx\",\"Verbose\":true,\"Status\":\"running\"}");
    }
}

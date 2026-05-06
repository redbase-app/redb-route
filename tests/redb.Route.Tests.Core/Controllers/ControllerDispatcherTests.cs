using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Controllers;
using redb.Route.Controllers.Attributes;

namespace redb.Route.Tests.Core.Controllers;

#region Test Controllers

[Route("modules")]
public class ModulesController : RedbController
{
    [HttpGet]
    public string[] GetAll() => ["module1", "module2"];

    [HttpGet("{id}")]
    public string GetById([FromRoute("id")] string id) => $"module-{id}";

    [HttpPost]
    public object Create([FromBody] CreateModuleRequest request) =>
        new { Name = request.Name, Created = true };

    [HttpDelete("{id}")]
    public void Delete([FromRoute("id")] string id) { }

    [HttpPut("{id}")]
    public object Update([FromRoute("id")] string id, [FromBody] CreateModuleRequest request) =>
        new { Id = id, Name = request.Name, Updated = true };
}

[Route("contexts")]
public class ContextsController : RedbController
{
    [HttpGet]
    public string[] List() => ["ctx1", "ctx2"];

    [HttpPost("{name}/start")]
    public object Start([FromRoute("name")] string name) =>
        new { Name = name, Started = true };

    [HttpGet("{name}/status")]
    public object Status([FromRoute("name")] string name, [FromQuery("verbose")] bool verbose) =>
        new { Name = name, Verbose = verbose, Status = "running" };
}

public class NoRouteController : RedbController
{
    [HttpGet]
    public string Health() => "ok";
}

public class CreateModuleRequest
{
    public string Name { get; set; } = "";
}

#endregion

public class ControllerRegistryTests
{
    [Fact]
    public void RegisterAssembly_discovers_controllers()
    {
        var registry = new ControllerRegistry();
        var count = registry.RegisterAssembly(typeof(ModulesController).Assembly);

        count.Should().BeGreaterThan(0);
        registry.Actions.Should().Contain(a => a.ControllerType == typeof(ModulesController));
        registry.Actions.Should().Contain(a => a.ControllerType == typeof(ContextsController));
    }

    [Fact]
    public void RegisterController_registers_all_attributed_methods()
    {
        var registry = new ControllerRegistry();
        var count = registry.RegisterController(typeof(ModulesController));

        count.Should().Be(5); // GetAll, GetById, Create, Delete, Update
    }

    [Fact]
    public void Resolve_matches_simple_path()
    {
        var registry = new ControllerRegistry();
        registry.RegisterController(typeof(ModulesController));

        var action = registry.Resolve(HttpMethodType.Get, "modules", out var routeParams);

        action.Should().NotBeNull();
        action!.Method.Name.Should().Be("GetAll");
        routeParams.Should().BeEmpty();
    }

    [Fact]
    public void Resolve_matches_path_with_parameter()
    {
        var registry = new ControllerRegistry();
        registry.RegisterController(typeof(ModulesController));

        var action = registry.Resolve(HttpMethodType.Get, "modules/123", out var routeParams);

        action.Should().NotBeNull();
        action!.Method.Name.Should().Be("GetById");
        routeParams.Should().ContainKey("id").WhoseValue.Should().Be("123");
    }

    [Fact]
    public void Resolve_matches_multi_segment_template()
    {
        var registry = new ControllerRegistry();
        registry.RegisterController(typeof(ContextsController));

        var action = registry.Resolve(HttpMethodType.Post, "contexts/myctx/start", out var routeParams);

        action.Should().NotBeNull();
        action!.Method.Name.Should().Be("Start");
        routeParams.Should().ContainKey("name").WhoseValue.Should().Be("myctx");
    }

    [Fact]
    public void Resolve_returns_null_for_no_match()
    {
        var registry = new ControllerRegistry();
        registry.RegisterController(typeof(ModulesController));

        var action = registry.Resolve(HttpMethodType.Get, "nonexistent/path", out _);

        action.Should().BeNull();
    }

    [Fact]
    public void Resolve_by_string_method()
    {
        var registry = new ControllerRegistry();
        registry.RegisterController(typeof(ModulesController));

        var action = registry.Resolve("POST", "modules", out _);

        action.Should().NotBeNull();
        action!.Method.Name.Should().Be("Create");
    }

    [Fact]
    public void Controller_without_RouteAttribute_uses_name_convention()
    {
        var registry = new ControllerRegistry();
        registry.RegisterController(typeof(NoRouteController));

        var action = registry.Resolve(HttpMethodType.Get, "noroute", out _);

        action.Should().NotBeNull();
        action!.Method.Name.Should().Be("Health");
    }
}

public class ControllerDispatcherProcessorTests
{
    [Fact]
    public async Task Dispatches_GET_to_controller()
    {
        var registry = new ControllerRegistry();
        registry.RegisterController(typeof(ModulesController));

        var context = new RouteContext();
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

        var context = new RouteContext();
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

        var context = new RouteContext();
        var dispatcher = new ControllerDispatcherProcessor(registry, context);

        var exchange = new Exchange();
        exchange.In.setHeader(ControllerDispatcherProcessor.PathHeader, "modules");
        exchange.In.setHeader(ControllerDispatcherProcessor.MethodHeader, "POST");
        exchange.In.Body = new CreateModuleRequest { Name = "test-module" };

        await dispatcher.Process(exchange);

        exchange.Out.Should().NotBeNull();
        exchange.Out!.GetHeader<int>("status.code").Should().Be(200);
    }

    [Fact]
    public async Task Returns_404_for_unknown_path()
    {
        var registry = new ControllerRegistry();
        registry.RegisterController(typeof(ModulesController));

        var context = new RouteContext();
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

        var context = new RouteContext();
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

        var context = new RouteContext();
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

        var context = new RouteContext();
        var dispatcher = new ControllerDispatcherProcessor(registry, context);

        var exchange = new Exchange();
        exchange.In.setHeader(ControllerDispatcherProcessor.PathHeader, "contexts/myctx/status");
        exchange.In.setHeader(ControllerDispatcherProcessor.MethodHeader, "GET");
        exchange.In.setHeader("query.verbose", "true");

        await dispatcher.Process(exchange);

        exchange.Out.Should().NotBeNull();
        exchange.Out!.GetHeader<int>("status.code").Should().Be(200);
    }
}

public class ParameterResolverTests
{
    [Fact]
    public void ConvertValue_handles_guid()
    {
        var guid = Guid.NewGuid();
        var result = ParameterResolver.ConvertValue(guid.ToString(), typeof(Guid));

        result.Should().Be(guid);
    }

    [Fact]
    public void ConvertValue_handles_int()
    {
        var result = ParameterResolver.ConvertValue("42", typeof(int));

        result.Should().Be(42);
    }

    [Fact]
    public void ConvertValue_handles_bool()
    {
        var result = ParameterResolver.ConvertValue("true", typeof(bool));

        result.Should().Be(true);
    }

    [Fact]
    public void ConvertValue_handles_null_for_reference_type()
    {
        var result = ParameterResolver.ConvertValue(null, typeof(string));

        result.Should().BeNull();
    }

    [Fact]
    public void ConvertValue_handles_null_for_value_type()
    {
        var result = ParameterResolver.ConvertValue(null, typeof(int));

        result.Should().Be(0);
    }
}

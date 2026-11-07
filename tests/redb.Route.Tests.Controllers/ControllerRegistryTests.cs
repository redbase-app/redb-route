using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Controllers;
using redb.Route.Controllers.Attributes;

namespace redb.Route.Tests.Controllers;

public class ControllerRegistryTests
{
    [Fact]
    public void RegisterAssembly_discovers_controllers()
    {
        var registry = new ControllerRegistry();
        var count = registry.RegisterAssembly(typeof(ModulesController).Assembly);

        // Every controller of this assembly with HTTP-attributed actions, and nothing else: a fixture that stops
        // being discovered, or a method-name fixture (EchoController, BindController, ...) that starts to be,
        // shows up here. Adding a fixture with [Http*] actions means adding it to this list.
        registry.Actions.Select(a => a.ControllerType).Distinct().Should().BeEquivalentTo(new[]
        {
            typeof(ModulesController), typeof(ContextsController), typeof(NoRouteController),
            typeof(ReturnShapesController), typeof(BindingController), typeof(FilterProbeController),
            typeof(SessionsLiteralFirstController), typeof(SessionsParamFirstController), typeof(SoapFareController),
            typeof(ControllerBadRequestTests.OrdersController), typeof(FacadeResponseMetaController),
            typeof(ControllerErrorLeakTests.LeakyController), typeof(ControllerErrorLeakTests.GrpcLeakyController),
            typeof(ControllerErrorLeakTests.NestedThrowController), typeof(ControllerErrorLeakTests.OkController),
            typeof(InstanceProbeController), typeof(ConstructorArgumentController),
            typeof(SelfReplyController),
        });
        count.Should().Be(39).And.Be(registry.Actions.Count);
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

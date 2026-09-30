using redb.Route.Controllers;
using redb.Route.Controllers.Attributes;

namespace redb.Route.Tests.Controllers;

/// <summary>
/// <see cref="ControllerRegistry.Resolve(HttpMethodType, string, out Dictionary{string, string})"/> ranks a literal
/// segment above a <c>{param}</c> at the same depth. The table is a <c>ConcurrentBag</c> with no defined order, so
/// both declaration orders are pinned: without the ranking one of them resolves the literal to the placeholder.
/// </summary>
public class RouteRankingTests
{
    public static IEnumerable<object[]> Controllers() =>
    [
        [typeof(SessionsLiteralFirstController), "sessions-literal-first"],
        [typeof(SessionsParamFirstController), "sessions-param-first"],
    ];

    [Theory]
    [MemberData(nameof(Controllers))]
    public void A_literal_segment_wins_over_a_placeholder(Type controller, string basePath)
    {
        var registry = new ControllerRegistry();
        registry.RegisterController(controller);

        var action = registry.Resolve(HttpMethodType.Delete, $"{basePath}/current", out var routeParams);

        action!.Method.Name.Should().Be("DeleteCurrent");
        routeParams.Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(Controllers))]
    public void Any_other_value_goes_to_the_placeholder(Type controller, string basePath)
    {
        var registry = new ControllerRegistry();
        registry.RegisterController(controller);

        var action = registry.Resolve(HttpMethodType.Delete, $"{basePath}/abc", out var routeParams);

        action!.Method.Name.Should().Be("DeleteById");
        routeParams.Should().ContainKey("sessionId").WhoseValue.Should().Be("abc");
    }

    [Fact]
    public void The_literal_wins_whichever_controller_was_registered_first()
    {
        // Registration order is what fills the bag; both orders must agree.
        foreach (var order in new[] { new[] { 0, 1 }, new[] { 1, 0 } })
        {
            var registry = new ControllerRegistry();
            foreach (var i in order)
                registry.RegisterController(i == 0 ? typeof(SessionsLiteralFirstController) : typeof(SessionsParamFirstController));

            registry.Resolve(HttpMethodType.Delete, "sessions-literal-first/current", out _)!.Method.Name.Should().Be("DeleteCurrent");
            registry.Resolve(HttpMethodType.Delete, "sessions-param-first/current", out _)!.Method.Name.Should().Be("DeleteCurrent");
        }
    }
}

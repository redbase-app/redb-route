using redb.Route.Definitions;

namespace redb.Route.Abstractions;

/// <summary>
/// Factory that creates <see cref="IRoutePolicy"/> instances for routes during compilation.
/// Registered via <see cref="IRouteContext.AddRoutePolicyFactory"/>.
/// When multiple factories are registered, the first non-null result wins.
/// </summary>
public interface IRoutePolicyFactory
{
    /// <summary>
    /// Creates a route policy for the given route, or returns <c>null</c> if this factory
    /// does not apply to the route.
    /// </summary>
    /// <param name="context">Route context.</param>
    /// <param name="routeId">Route identifier.</param>
    /// <param name="definition">Route definition (access <c>GetCluster()</c>, etc.).</param>
    /// <returns>A route policy, or <c>null</c> if this factory does not handle this route.</returns>
    IRoutePolicy? CreateRoutePolicy(IRouteContext context, string routeId, RouteDefinition definition);
}

using Microsoft.Extensions.DependencyInjection;
using redb.Route.Extensions;

namespace redb.Route.IbmMq;

/// <summary>
/// Extension methods for registering the IBM MQ transport in a DI container.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the <see cref="IbmMqComponent"/> in the route context so that
    /// <c>ibmmq:</c> URIs are resolved.
    /// <example>
    /// <code>
    /// services.AddRedbRoute(route =&gt;
    /// {
    ///     route.Services.AddRedbRouteIbmMq();
    ///     route.AddRouteBuilder&lt;MyRoutes&gt;();
    /// });
    /// </code>
    /// </example>
    /// </summary>
    public static IServiceCollection AddRedbRouteIbmMq(this IServiceCollection services)
    {
        // IRouteContextConfigurator is applied by RouteHostedService at startup --
        // the correct registration hook (a lazy marker singleton never fires).
        services.AddRouteComponent<IbmMqComponent>();

        return services;
    }
}


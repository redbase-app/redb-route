using Microsoft.Extensions.DependencyInjection;
using redb.Route.Extensions;

namespace redb.Route.Tcp;

/// <summary>
/// Extension methods for registering the TCP component with dependency injection.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the TCP component with the route context.
    /// </summary>
    public static IServiceCollection AddRedbRouteTcp(this IServiceCollection services)
    {
        // IRouteContextConfigurator is applied by RouteHostedService at startup --
        // the correct registration hook (a lazy marker singleton never fires).
        services.AddRouteComponent<TcpComponent>();

        return services;
    }
}


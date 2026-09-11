using Microsoft.Extensions.DependencyInjection;
using redb.Route.Extensions;

namespace redb.Route.Exec;

/// <summary>
/// Extension methods for registering the exec component with dependency injection.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the exec component with the route context.
    /// </summary>
    public static IServiceCollection AddRedbRouteExec(this IServiceCollection services)
    {
        // IRouteContextConfigurator is applied by RouteHostedService at startup --
        // the correct registration hook (a lazy marker singleton never fires).
        services.AddRouteComponent<ExecComponent>();

        return services;
    }
}


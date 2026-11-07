using Microsoft.Extensions.DependencyInjection;
using redb.Route.Extensions;
using redb.Route.Http;

namespace redb.Route.As2;

/// <summary>
/// Extension methods for registering the AS2 transport in a DI container.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the <see cref="As2Component"/> in the route context so that <c>as2://</c> and
    /// <c>as2s://</c> URIs are resolved.
    /// <example>
    /// <code>
    /// services.AddRedbRoute(route =&gt;
    /// {
    ///     route.Services.AddRedbRouteAs2();
    ///     route.AddRouteBuilder&lt;MyRoutes&gt;();
    /// });
    /// </code>
    /// </example>
    /// </summary>
    public static IServiceCollection AddRedbRouteAs2(this IServiceCollection services)
    {
        // Share ONE Kestrel host with every other HTTP-based connector in this worker (idempotent).
        services.AddRedbRouteHttpHosting();
        services.AddSingleton<As2Component>();

        // IRouteContextConfigurator is applied by RouteHostedService at startup --
        // the correct registration hook (a lazy marker singleton never fires).
        services.AddRouteContextConfigurator((sp, context) =>
        {
            var component = sp.GetRequiredService<As2Component>();
            component.ServerManager = sp.GetRequiredService<SharedHttpServerManager>();
            context.AddComponent(component);   // registers as2 + as2s, sets Context + Logger
        });

        return services;
    }
}


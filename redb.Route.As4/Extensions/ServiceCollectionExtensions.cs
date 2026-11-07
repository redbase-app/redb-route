using Microsoft.Extensions.DependencyInjection;
using redb.Route.Extensions;
using redb.Route.Http;

namespace redb.Route.As4;

/// <summary>Registration of the AS4 transport in a DI container.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the <see cref="As4Component"/> so that <c>as4:</c> and <c>as4s:</c> URIs resolve, sharing one
    /// Kestrel host with every other HTTP-based connector of the process.
    /// <example><code>
    /// services.AddRedbRoute(route =&gt;
    /// {
    ///     route.Services.AddRedbRouteAs4();
    ///     route.AddRouteBuilder&lt;MyRoutes&gt;();
    /// });
    /// </code></example>
    /// </summary>
    public static IServiceCollection AddRedbRouteAs4(this IServiceCollection services)
    {
        services.AddRedbRouteHttpHosting();
        services.AddSingleton<As4Component>();

        services.AddRouteContextConfigurator((sp, context) =>
        {
            var component = sp.GetRequiredService<As4Component>();
            component.ServerManager = sp.GetRequiredService<SharedHttpServerManager>();
            context.AddComponent(component);   // registers as4 + as4s, sets Context + Logger
        });

        return services;
    }
}

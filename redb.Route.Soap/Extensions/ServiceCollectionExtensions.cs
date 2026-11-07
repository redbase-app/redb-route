using Microsoft.Extensions.DependencyInjection;
using redb.Route.Extensions;
using redb.Route.Http;

namespace redb.Route.Soap;

/// <summary>
/// Extension methods for registering the SOAP transport in a DI container.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the <see cref="SoapComponent"/> so that <c>soap://</c> and <c>soaps://</c> URIs are resolved.
    /// <example><code>
    /// services.AddRedbRoute(route =>
    /// {
    ///     route.Services.AddRedbRouteSoap();
    ///     route.AddRouteBuilder&lt;MyRoutes&gt;();
    /// });
    /// </code></example>
    /// </summary>
    public static IServiceCollection AddRedbRouteSoap(this IServiceCollection services)
    {
        // Share ONE Kestrel host with every other HTTP-based connector in this worker (idempotent).
        services.AddRedbRouteHttpHosting();
        services.AddSingleton<SoapComponent>();

        // IRouteContextConfigurator is applied by RouteHostedService at startup --
        // the correct registration hook (a lazy marker singleton never fires).
        services.AddRouteContextConfigurator((sp, context) =>
        {
            var component = sp.GetRequiredService<SoapComponent>();
            component.ServerManager = sp.GetRequiredService<SharedHttpServerManager>();
            context.AddComponent(component);   // registers soap + soaps, sets Context + Logger
        });

        return services;
    }
}


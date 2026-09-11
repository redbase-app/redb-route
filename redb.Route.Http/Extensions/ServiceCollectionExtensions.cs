using Microsoft.Extensions.DependencyInjection;
using redb.Route.Extensions;

namespace redb.Route.Http;

/// <summary>
/// Extension methods for registering the HTTP component with dependency injection.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the HTTP and HTTPS components with the route context.
    /// </summary>
    public static IServiceCollection AddRedbRouteHttp(this IServiceCollection services)
        => AddRedbRouteHttp(services, null);

    /// <summary>
    /// Registers the HTTP and HTTPS components with global CORS configuration.
    /// <para>Example: <c>services.AddRedbRouteHttp(cors =&gt; { cors.Enabled = true; cors.Origins = "https://example.com"; });</c></para>
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configureCors">Optional action to configure global CORS defaults for all HTTP consumers.</param>
    public static IServiceCollection AddRedbRouteHttp(this IServiceCollection services, Action<HttpCorsOptions>? configureCors)
    {
        // Shared server manager — single instance across all route contexts and HTTP-based connectors.
        services.AddRedbRouteHttpHosting();

        services.AddSingleton(sp =>
        {
            var component = new HttpComponent();
            component.ServerManager = sp.GetRequiredService<SharedHttpServerManager>();
            configureCors?.Invoke(component.DefaultCors);
            return component;
        });

        services.AddSingleton(sp =>
        {
            var component = new HttpsComponent();
            component.ServerManager = sp.GetRequiredService<SharedHttpServerManager>();
            configureCors?.Invoke(component.DefaultCors);
            return component;
        });

        // IRouteContextConfigurator is applied by RouteHostedService at startup --
        // the correct registration hook (a lazy marker singleton never fires).
        services.AddRouteContextConfigurator((sp, context) =>
        {
            context.AddComponent(sp.GetRequiredService<HttpComponent>());
            context.AddComponent(sp.GetRequiredService<HttpsComponent>());
        });

        return services;
    }
}

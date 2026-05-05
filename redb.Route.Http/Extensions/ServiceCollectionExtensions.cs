using Microsoft.Extensions.DependencyInjection;
using redb.Route.Abstractions;

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
        // Shared server manager — single instance across all route contexts
        services.AddSingleton<SharedHttpServerManager>();

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

        services.AddSingleton<IHttpComponentRegistrar>(sp =>
        {
            var context = sp.GetRequiredService<IRouteContext>();

            var httpComponent = sp.GetRequiredService<HttpComponent>();
            context.AddComponent(httpComponent);

            var httpsComponent = sp.GetRequiredService<HttpsComponent>();
            context.AddComponent(httpsComponent);

            return new HttpComponentRegistrar();
        });

        return services;
    }
}

internal interface IHttpComponentRegistrar;
internal sealed class HttpComponentRegistrar : IHttpComponentRegistrar;

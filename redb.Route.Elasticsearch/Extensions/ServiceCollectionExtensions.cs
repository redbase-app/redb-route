using Microsoft.Extensions.DependencyInjection;
using redb.Route.Abstractions;

namespace redb.Route.Elasticsearch;

/// <summary>
/// Extension methods for registering the Elasticsearch transport in a DI container.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the <see cref="ElasticsearchComponent"/> in the route context so that
    /// <c>elasticsearch://</c> and <c>es://</c> URIs are resolved.
    /// <example>
    /// <code>
    /// services.AddRedbRoute(route =&gt;
    /// {
    ///     route.Services.AddRedbRouteElasticsearch();
    ///     route.AddRouteBuilder&lt;MyRoutes&gt;();
    /// });
    /// </code>
    /// </example>
    /// </summary>
    public static IServiceCollection AddRedbRouteElasticsearch(this IServiceCollection services)
    {
        services.AddSingleton<ElasticsearchComponent>();

        // Post-configure: register "elasticsearch" + "es" (via AlternateSchemes)
        services.AddSingleton<IElasticsearchComponentRegistrar>(sp =>
        {
            var context = sp.GetRequiredService<IRouteContext>();
            var component = sp.GetRequiredService<ElasticsearchComponent>();
            context.AddComponent(component);

            return new ElasticsearchComponentRegistrar();
        });

        return services;
    }
}

/// <summary>Marker interface for DI registration.</summary>
internal interface IElasticsearchComponentRegistrar;

/// <summary>Marker registration for DI.</summary>
internal sealed class ElasticsearchComponentRegistrar : IElasticsearchComponentRegistrar;

using Microsoft.Extensions.DependencyInjection;
using redb.Route.Extensions;

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
        // IRouteContextConfigurator is applied by RouteHostedService at startup --
        // the correct registration hook (a lazy marker singleton never fires).
        services.AddRouteComponent<ElasticsearchComponent>();

        return services;
    }
}


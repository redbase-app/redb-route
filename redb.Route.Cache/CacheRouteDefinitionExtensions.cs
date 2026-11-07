using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using redb.Route.Abstractions;
using redb.Route.Extensions;

namespace redb.Route.Cache;

/// <summary>DSL entry points and registration of the cache EIP.</summary>
public static class CacheRouteDefinitionExtensions
{
    /// <summary>
    /// Opens a caching scope keyed by a route-language template (<c>"customer-${header.customerId}"</c>).
    /// On a hit the inner steps are skipped and the cached body is the message; on a miss they run and
    /// the result is stored for <paramref name="ttl"/>. Close with <c>EndCache()</c>.
    /// </summary>
    public static CacheDefinition Cache(this IRouteDefinition route, string keyTemplate, TimeSpan? ttl = null)
        => Cache(route, CacheDefinition.KeyFromTemplate(keyTemplate), ttl);

    /// <summary>Opens a caching scope with a key computed by <paramref name="key"/>.</summary>
    public static CacheDefinition Cache(this IRouteDefinition route, Func<IExchange, string> key, TimeSpan? ttl = null)
    {
        ArgumentNullException.ThrowIfNull(route);
        var definition = new CacheDefinition(key, ttl) { Parent = route };
        route.Outputs.Add(definition);   // the public tree API, identical to ProcessorDefinition.AddOutput
        return definition;
    }

    /// <summary>Registers options and the <c>cache:</c> component on a context built without DI.</summary>
    public static IRouteContext UseCache(this IRouteContext context, Action<RouteCacheOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        var options = new RouteCacheOptions();
        configure?.Invoke(options);
        context.AddService(typeof(RouteCacheOptions), options);
        if (!context.GetComponentNames().Contains("cache", StringComparer.OrdinalIgnoreCase))
            context.AddComponent(new CacheComponent());
        return context;
    }

    /// <summary>DI registration: options plus the <c>cache:</c> component on the hosted context.</summary>
    public static IServiceCollection AddRedbRouteCache(this IServiceCollection services, Action<RouteCacheOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var options = new RouteCacheOptions();
        configure?.Invoke(options);
        services.TryAddSingleton(options);
        // IRouteContextConfigurator is applied by RouteHostedService at startup --
        // the correct registration hook (a lazy marker singleton never fires).
        services.AddRouteComponent<CacheComponent>();
        return services;
    }
}

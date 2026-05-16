using Microsoft.Extensions.DependencyInjection;
using redb.Route.Abstractions;

namespace redb.Route.Redis;

/// <summary>
/// Extension methods for registering the Redis transport in a DI container.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the <see cref="RedisComponent"/> in the route context so that
    /// <c>redis:</c> URIs are resolved.
    /// <example>
    /// <code>
    /// services.AddRedbRoute(route =&gt;
    /// {
    ///     route.Services.AddRedbRouteRedis();
    ///     route.AddRouteBuilder&lt;MyRoutes&gt;();
    /// });
    /// </code>
    /// </example>
    /// </summary>
    public static IServiceCollection AddRedbRouteRedis(this IServiceCollection services)
    {
        services.AddSingleton<RedisComponent>();

        // Post-configure: register the component in the route context after engine start
        services.AddSingleton<IRedisComponentRegistrar>(sp =>
        {
            var context = sp.GetRequiredService<IRouteContext>();
            var component = sp.GetRequiredService<RedisComponent>();
            context.AddComponent(component);
            return new RedisComponentRegistrar();
        });

        return services;
    }
}

/// <summary>Marker interface for DI registration.</summary>
internal interface IRedisComponentRegistrar;

/// <summary>Marker registration for DI.</summary>
internal sealed class RedisComponentRegistrar : IRedisComponentRegistrar;

using Microsoft.Extensions.DependencyInjection;
using redb.Route.Abstractions;

namespace redb.Route.IbmMq;

/// <summary>
/// Extension methods for registering the IBM MQ transport in a DI container.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the <see cref="IbmMqComponent"/> in the route context so that
    /// <c>ibmmq:</c> URIs are resolved.
    /// <example>
    /// <code>
    /// services.AddRedbRoute(route =&gt;
    /// {
    ///     route.Services.AddRedbRouteIbmMq();
    ///     route.AddRouteBuilder&lt;MyRoutes&gt;();
    /// });
    /// </code>
    /// </example>
    /// </summary>
    public static IServiceCollection AddRedbRouteIbmMq(this IServiceCollection services)
    {
        services.AddSingleton<IbmMqComponent>();

        services.AddSingleton<IIbmMqComponentRegistrar>(sp =>
        {
            var context = sp.GetRequiredService<IRouteContext>();
            var component = sp.GetRequiredService<IbmMqComponent>();
            context.AddComponent(component);
            return new IbmMqComponentRegistrar();
        });

        return services;
    }
}

/// <summary>Marker interface for DI registration.</summary>
internal interface IIbmMqComponentRegistrar;

/// <summary>Marker registration for DI.</summary>
internal sealed class IbmMqComponentRegistrar : IIbmMqComponentRegistrar;

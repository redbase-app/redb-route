using Microsoft.Extensions.DependencyInjection;
using redb.Route.Abstractions;

namespace redb.Route.Kafka;

/// <summary>
/// Extension methods for registering the Kafka transport in a DI container.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the <see cref="KafkaComponent"/> in the route context so that
    /// <c>kafka://</c> URIs are resolved.
    /// <example>
    /// <code>
    /// services.AddRedbRoute(route =&gt;
    /// {
    ///     route.Services.AddRedbRouteKafka();
    ///     route.AddRouteBuilder&lt;MyRoutes&gt;();
    /// });
    /// </code>
    /// </example>
    /// </summary>
    public static IServiceCollection AddRedbRouteKafka(this IServiceCollection services)
    {
        services.AddSingleton<KafkaComponent>();

        // Post-configure: register the component in the route context after engine start
        services.AddSingleton<IKafkaComponentRegistrar>(sp =>
        {
            var context = sp.GetRequiredService<IRouteContext>();
            var component = sp.GetRequiredService<KafkaComponent>();
            context.AddComponent(component);
            return new KafkaComponentRegistrar();
        });

        return services;
    }
}

/// <summary>Marker interface for DI registration.</summary>
internal interface IKafkaComponentRegistrar;

/// <summary>Marker registration for DI.</summary>
internal sealed class KafkaComponentRegistrar : IKafkaComponentRegistrar;



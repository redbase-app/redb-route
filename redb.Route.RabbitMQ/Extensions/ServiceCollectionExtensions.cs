using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;

namespace redb.Route.RabbitMQ;

/// <summary>
/// Extension methods for registering the RabbitMQ transport in a DI container.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the <see cref="RabbitMQComponent"/> in the route context so that
    /// <c>rabbitmq://</c> URIs are resolved.
    /// <example>
    /// <code>
    /// services.AddRedbRoute(route =&gt;
    /// {
    ///     route.Services.AddRedbRouteRabbitMQ();
    ///     route.AddRouteBuilder&lt;MyRoutes&gt;();
    /// });
    /// </code>
    /// </example>
    /// </summary>
    public static IServiceCollection AddRedbRouteRabbitMQ(this IServiceCollection services)
    {
        services.AddSingleton<RabbitMQComponent>();

        services.AddSingleton<IRabbitMQComponentRegistrar>(sp =>
        {
            var context = sp.GetRequiredService<IRouteContext>();
            var component = sp.GetRequiredService<RabbitMQComponent>();
            context.AddComponent(component);
            return new RabbitMQComponentRegistrar();
        });

        return services;
    }
}

/// <summary>Marker interface for DI registration.</summary>
internal interface IRabbitMQComponentRegistrar;

/// <summary>Marker registration for DI.</summary>
internal sealed class RabbitMQComponentRegistrar : IRabbitMQComponentRegistrar;

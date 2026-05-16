using Microsoft.Extensions.DependencyInjection;
using redb.Route.Abstractions;

namespace redb.Route.Amqp;

/// <summary>
/// Extension methods for registering the AMQP 1.0 transport in a DI container.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the <see cref="AmqpComponent"/> in the route context so that
    /// <c>amqp://</c> URIs are resolved.
    /// <example>
    /// <code>
    /// services.AddRedbRoute(route =&gt;
    /// {
    ///     route.Services.AddRedbRouteAmqp();
    ///     route.AddRouteBuilder&lt;MyRoutes&gt;();
    /// });
    /// </code>
    /// </example>
    /// </summary>
    public static IServiceCollection AddRedbRouteAmqp(this IServiceCollection services)
    {
        services.AddSingleton<AmqpComponent>();

        services.AddSingleton<IAmqpComponentRegistrar>(sp =>
        {
            var context = sp.GetRequiredService<IRouteContext>();
            var component = sp.GetRequiredService<AmqpComponent>();
            context.AddComponent(component);
            return new AmqpComponentRegistrar();
        });

        return services;
    }
}

/// <summary>Marker interface for DI registration.</summary>
internal interface IAmqpComponentRegistrar;

/// <summary>Marker registration for DI.</summary>
internal sealed class AmqpComponentRegistrar : IAmqpComponentRegistrar;

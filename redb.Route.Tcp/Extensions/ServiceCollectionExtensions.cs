using Microsoft.Extensions.DependencyInjection;
using redb.Route.Abstractions;

namespace redb.Route.Tcp;

/// <summary>
/// Extension methods for registering the TCP component with dependency injection.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the TCP component with the route context.
    /// </summary>
    public static IServiceCollection AddRedbRouteTcp(this IServiceCollection services)
    {
        services.AddSingleton<TcpComponent>();

        services.AddSingleton<ITcpComponentRegistrar>(sp =>
        {
            var context = sp.GetRequiredService<IRouteContext>();
            var component = sp.GetRequiredService<TcpComponent>();
            context.AddComponent(component);
            return new TcpComponentRegistrar();
        });

        return services;
    }
}

internal interface ITcpComponentRegistrar;
internal sealed class TcpComponentRegistrar : ITcpComponentRegistrar;

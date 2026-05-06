using Microsoft.Extensions.DependencyInjection;
using redb.Route.Abstractions;

namespace redb.Route.WebSocket;

/// <summary>
/// Extension methods for registering the WebSocket component with dependency injection.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the WS and WSS components with the route context.
    /// </summary>
    public static IServiceCollection AddRedbRouteWebSocket(this IServiceCollection services)
    {
        services.AddSingleton<WsComponent>();
        services.AddSingleton<WssComponent>();

        services.AddSingleton<IWsComponentRegistrar>(sp =>
        {
            var context = sp.GetRequiredService<IRouteContext>();

            var ws = sp.GetRequiredService<WsComponent>();
            context.AddComponent(ws);

            var wss = sp.GetRequiredService<WssComponent>();
            context.AddComponent(wss);

            return new WsComponentRegistrar();
        });

        return services;
    }
}

internal interface IWsComponentRegistrar;
internal sealed class WsComponentRegistrar : IWsComponentRegistrar;

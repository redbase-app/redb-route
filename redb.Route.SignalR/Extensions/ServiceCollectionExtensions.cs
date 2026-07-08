using Microsoft.Extensions.DependencyInjection;
using redb.Route.Abstractions;

namespace redb.Route.SignalR;

/// <summary>
/// Extension methods for registering the SignalR component with dependency injection.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the SignalR component with the route context.
    /// </summary>
    public static IServiceCollection AddRedbRouteSignalR(this IServiceCollection services)
    {
        services.AddSingleton<SignalRComponent>();

        services.AddSingleton<ISignalRComponentRegistrar>(sp =>
        {
            var context = sp.GetRequiredService<IRouteContext>();
            var component = sp.GetRequiredService<SignalRComponent>();
            context.AddComponent(component);
            return new SignalRComponentRegistrar();
        });

        return services;
    }
}

internal interface ISignalRComponentRegistrar;
internal sealed class SignalRComponentRegistrar : ISignalRComponentRegistrar;

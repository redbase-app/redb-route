using Microsoft.Extensions.DependencyInjection;
using redb.Route.Abstractions;

namespace redb.Route.Exec;

/// <summary>
/// Extension methods for registering the exec component with dependency injection.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the exec component with the route context.
    /// </summary>
    public static IServiceCollection AddRedbRouteExec(this IServiceCollection services)
    {
        services.AddSingleton<ExecComponent>();

        services.AddSingleton<IExecComponentRegistrar>(sp =>
        {
            var context = sp.GetRequiredService<IRouteContext>();
            var component = sp.GetRequiredService<ExecComponent>();
            context.AddComponent(component);
            return new ExecComponentRegistrar();
        });

        return services;
    }
}

internal interface IExecComponentRegistrar;
internal sealed class ExecComponentRegistrar : IExecComponentRegistrar;

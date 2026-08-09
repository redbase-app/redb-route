using Microsoft.Extensions.DependencyInjection;
using redb.Route.Abstractions;
using redb.Route.Http;

namespace redb.Route.As2;

/// <summary>
/// Extension methods for registering the AS2 transport in a DI container.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the <see cref="As2Component"/> in the route context so that <c>as2://</c> and
    /// <c>as2s://</c> URIs are resolved.
    /// <example>
    /// <code>
    /// services.AddRedbRoute(route =&gt;
    /// {
    ///     route.Services.AddRedbRouteAs2();
    ///     route.AddRouteBuilder&lt;MyRoutes&gt;();
    /// });
    /// </code>
    /// </example>
    /// </summary>
    public static IServiceCollection AddRedbRouteAs2(this IServiceCollection services)
    {
        // Share ONE Kestrel host with every other HTTP-based connector in this worker (idempotent).
        services.AddRedbRouteHttpHosting();
        services.AddSingleton<As2Component>();

        services.AddSingleton<IAs2ComponentRegistrar>(sp =>
        {
            var context = sp.GetRequiredService<IRouteContext>();
            var component = sp.GetRequiredService<As2Component>();
            component.ServerManager = sp.GetRequiredService<SharedHttpServerManager>();
            context.AddComponent(component);   // registers as2 + as2s, sets Context + Logger
            return new As2ComponentRegistrar();
        });

        return services;
    }
}

/// <summary>Marker interface for DI registration.</summary>
internal interface IAs2ComponentRegistrar;

/// <summary>Marker registration for DI.</summary>
internal sealed class As2ComponentRegistrar : IAs2ComponentRegistrar;

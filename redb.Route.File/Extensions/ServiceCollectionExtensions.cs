using Microsoft.Extensions.DependencyInjection;
using redb.Route.Abstractions;

namespace redb.Route.File;

/// <summary>
/// Extension methods for registering the File transport in a DI container.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the <see cref="FileComponent"/> in the route context so that
    /// <c>file://</c> URIs are resolved.
    /// <example>
    /// <code>
    /// services.AddRedbRoute(route =&gt;
    /// {
    ///     route.Services.AddRedbRouteFile();
    ///     route.AddRouteBuilder&lt;MyRoutes&gt;();
    /// });
    /// </code>
    /// </example>
    /// </summary>
    public static IServiceCollection AddRedbRouteFile(this IServiceCollection services)
    {
        services.AddSingleton<FileComponent>();

        // Post-configure: register the component in the route context after engine start
        services.AddSingleton<IFileComponentRegistrar>(sp =>
        {
            var context = sp.GetRequiredService<IRouteContext>();
            var component = sp.GetRequiredService<FileComponent>();
            context.AddComponent(component);
            return new FileComponentRegistrar();
        });

        return services;
    }
}

/// <summary>Marker interface for DI registration.</summary>
internal interface IFileComponentRegistrar;

/// <summary>Marker registration for DI.</summary>
internal sealed class FileComponentRegistrar : IFileComponentRegistrar;

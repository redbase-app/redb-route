using Microsoft.Extensions.DependencyInjection;
using redb.Route.Abstractions;

namespace redb.Route.Sftp;

/// <summary>
/// Extension methods for registering the SFTP transport in a DI container.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the <see cref="SftpComponent"/> in the route context so that
    /// <c>sftp://</c> URIs are resolved.
    /// <example>
    /// <code>
    /// services.AddRedbRoute(route =&gt;
    /// {
    ///     route.Services.AddRedbRouteSftp();
    ///     route.AddRouteBuilder&lt;MyRoutes&gt;();
    /// });
    /// </code>
    /// </example>
    /// </summary>
    public static IServiceCollection AddRedbRouteSftp(this IServiceCollection services)
    {
        services.AddSingleton<SftpComponent>();

        // Post-configure: register the component in the route context after engine start
        services.AddSingleton<ISftpComponentRegistrar>(sp =>
        {
            var context = sp.GetRequiredService<IRouteContext>();
            var component = sp.GetRequiredService<SftpComponent>();
            context.AddComponent(component);
            return new SftpComponentRegistrar();
        });

        return services;
    }
}

/// <summary>Marker interface for DI registration.</summary>
internal interface ISftpComponentRegistrar;

/// <summary>Marker registration for DI.</summary>
internal sealed class SftpComponentRegistrar : ISftpComponentRegistrar;

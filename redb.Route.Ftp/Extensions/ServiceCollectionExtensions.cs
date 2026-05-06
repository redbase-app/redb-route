using Microsoft.Extensions.DependencyInjection;
using redb.Route.Abstractions;

namespace redb.Route.Ftp;

/// <summary>
/// Extension methods for registering the FTP transport in a DI container.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the <see cref="FtpComponent"/> in the route context so that
    /// <c>ftp://</c> URIs are resolved.
    /// <example>
    /// <code>
    /// services.AddRedbRoute(route =&gt;
    /// {
    ///     route.Services.AddRedbRouteFtp();
    ///     route.AddRouteBuilder&lt;MyRoutes&gt;();
    /// });
    /// </code>
    /// </example>
    /// </summary>
    public static IServiceCollection AddRedbRouteFtp(this IServiceCollection services)
    {
        services.AddSingleton<FtpComponent>();

        services.AddSingleton<IFtpComponentRegistrar>(sp =>
        {
            var context = sp.GetRequiredService<IRouteContext>();
            var component = sp.GetRequiredService<FtpComponent>();
            context.AddComponent(component);
            return new FtpComponentRegistrar();
        });

        return services;
    }
}

/// <summary>Marker interface for DI registration.</summary>
internal interface IFtpComponentRegistrar;

/// <summary>Marker registration for DI.</summary>
internal sealed class FtpComponentRegistrar : IFtpComponentRegistrar;

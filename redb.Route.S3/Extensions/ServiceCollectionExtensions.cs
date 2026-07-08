using Microsoft.Extensions.DependencyInjection;
using redb.Route.Abstractions;

namespace redb.Route.S3;

/// <summary>
/// Extension methods for registering the S3 transport in a DI container.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the <see cref="S3Component"/> in the route context so that
    /// <c>s3://</c> URIs are resolved.
    /// <example>
    /// <code>
    /// services.AddRedbRoute(route =&gt;
    /// {
    ///     route.Services.AddRedbRouteS3();
    ///     route.AddRouteBuilder&lt;MyRoutes&gt;();
    /// });
    /// </code>
    /// </example>
    /// </summary>
    public static IServiceCollection AddRedbRouteS3(this IServiceCollection services)
    {
        services.AddSingleton<S3Component>();

        // Post-configure: register the component in the route context after engine start
        services.AddSingleton<IS3ComponentRegistrar>(sp =>
        {
            var context = sp.GetRequiredService<IRouteContext>();
            var component = sp.GetRequiredService<S3Component>();
            context.AddComponent(component);
            return new S3ComponentRegistrar();
        });

        return services;
    }
}

/// <summary>Marker interface for DI registration.</summary>
internal interface IS3ComponentRegistrar;

/// <summary>Marker registration for DI.</summary>
internal sealed class S3ComponentRegistrar : IS3ComponentRegistrar;

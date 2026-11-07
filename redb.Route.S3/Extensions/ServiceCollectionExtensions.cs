using Microsoft.Extensions.DependencyInjection;
using redb.Route.Extensions;

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
        // IRouteContextConfigurator is applied by RouteHostedService at startup --
        // the correct registration hook (a lazy marker singleton never fires).
        services.AddRouteComponent<S3Component>();

        return services;
    }
}


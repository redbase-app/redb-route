using Microsoft.Extensions.DependencyInjection;
using redb.Route.Extensions;

namespace redb.Route.Ldap;

/// <summary>
/// Extension methods for registering the LDAP transport in a DI container.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the LdapComponent in the route context so that ldap: URIs are resolved.
    /// <example>
    /// <code>
    /// services.AddRedbRoute(route =>
    /// {
    ///     route.Services.AddRedbRouteLdap();
    ///     route.AddRouteBuilder&lt;MyRoutes&gt;();
    /// });
    /// </code>
    /// </example>
    /// </summary>
    public static IServiceCollection AddRedbRouteLdap(this IServiceCollection services)
    {
        // IRouteContextConfigurator is applied by RouteHostedService at startup --
        // the correct registration hook (a lazy marker singleton never fires).
        services.AddRouteComponent<LdapComponent>();

        return services;
    }
}


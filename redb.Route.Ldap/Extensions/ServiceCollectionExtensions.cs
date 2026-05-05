using Microsoft.Extensions.DependencyInjection;
using redb.Route.Abstractions;

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
        services.AddSingleton<LdapComponent>();

        services.AddSingleton<ILdapComponentRegistrar>(sp =>
        {
            var context = sp.GetRequiredService<IRouteContext>();
            var component = sp.GetRequiredService<LdapComponent>();
            context.AddComponent(component);
            return new LdapComponentRegistrar();
        });

        return services;
    }
}

/// <summary>Marker interface for DI registration.</summary>
internal interface ILdapComponentRegistrar;

/// <summary>Marker registration for DI.</summary>
internal sealed class LdapComponentRegistrar : ILdapComponentRegistrar;

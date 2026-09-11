using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace redb.Route.Http;

/// <summary>
/// Registration for the shared Kestrel hosting infrastructure.
/// </summary>
public static class HostingServiceCollectionExtensions
{
    /// <summary>
    /// Registers the shared <see cref="SharedHttpServerManager"/> as a single instance for the container.
    /// Idempotent (<see cref="ServiceCollectionDescriptorExtensions.TryAddSingleton{TService}(IServiceCollection)"/>)
    /// so every HTTP-based transport (<c>redb.Route.Http</c>, <c>redb.Route.As2</c>, …) can call it and they all
    /// share ONE server manager — one Kestrel per host:port, with routes multiplexed across connectors.
    /// </summary>
    public static IServiceCollection AddRedbRouteHttpHosting(this IServiceCollection services)
        => AddRedbRouteHttpHosting(services, null);

    /// <summary>
    /// Registers the shared host and applies <paramref name="configure"/> to its
    /// <see cref="HttpHostingOptions"/>. Safe to call more than once: every call contributes its
    /// configuration, the manager is still created once, and the options are read when the manager
    /// is first resolved. Connectors register the host with no configuration; the process that
    /// owns the deployment (a worker, a test fixture) is the one that adds trusted proxies.
    /// <para>Example: <c>services.AddRedbRouteHttpHosting(o =&gt; o.TrustedProxies.Add("10.0.0.5"));</c></para>
    /// </summary>
    public static IServiceCollection AddRedbRouteHttpHosting(
        this IServiceCollection services,
        Action<HttpHostingOptions>? configure)
    {
        services.AddOptions();
        if (configure is not null)
            services.Configure(configure);

        services.TryAddSingleton(sp =>
            new SharedHttpServerManager(sp.GetRequiredService<IOptions<HttpHostingOptions>>().Value));

        return services;
    }
}

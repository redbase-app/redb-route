using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

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
    {
        services.TryAddSingleton<SharedHttpServerManager>();
        return services;
    }
}

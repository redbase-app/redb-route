using Microsoft.Extensions.DependencyInjection;

namespace redb.Route.AzureServiceBus.Extensions;

/// <summary>
/// DI registration for the Azure Service Bus component.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="AzureServiceBusComponent"/> as a singleton.
    /// Use with <c>route.AddComponent&lt;AzureServiceBusComponent&gt;()</c> or resolve from DI.
    /// </summary>
    public static IServiceCollection AddRedbRouteAzureServiceBus(this IServiceCollection services)
    {
        services.AddSingleton<AzureServiceBusComponent>();
        return services;
    }
}

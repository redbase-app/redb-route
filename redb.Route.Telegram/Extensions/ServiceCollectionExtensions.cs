using Microsoft.Extensions.DependencyInjection;
using redb.Route.Core;
using redb.Route.Extensions;

namespace redb.Route.Telegram;

/// <summary>
/// Extension methods for registering the Telegram transport in a DI container.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="TelegramComponent"/> so that <c>telegram://</c> URIs are resolved.
    /// <example><code>
    /// services.AddRedbRoute(route =>
    /// {
    ///     route.Services.AddRedbRouteTelegram();
    ///     route.AddRouteBuilder&lt;MyTelegramRoutes&gt;();
    /// });
    /// </code></example>
    /// </summary>
    public static IServiceCollection AddRedbRouteTelegram(this IServiceCollection services)
    {
        services.AddSingleton<TelegramComponent>();

        // IRouteContextConfigurator is resolved by RouteHostedService at startup via
        // IEnumerable<IRouteContextConfigurator> — this is the correct registration hook.
        services.AddSingleton<IRouteContextConfigurator>(sp =>
            new TelegramComponentConfigurator(sp));

        return services;
    }
}

/// <summary>Registers <see cref="TelegramComponent"/> into the route context at startup.</summary>
internal sealed class TelegramComponentConfigurator(IServiceProvider sp) : IRouteContextConfigurator
{
    public void Configure(RouteContext context) =>
        context.AddComponent(sp.GetRequiredService<TelegramComponent>());
}

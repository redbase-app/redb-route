using Microsoft.Extensions.DependencyInjection;
using redb.Route.Extensions;

namespace redb.Route.Quartz;

/// <summary>
/// Extension methods for registering Quartz scheduling components in a DI container.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers both <see cref="CronComponent"/> and <see cref="QuartzTimerComponent"/>
    /// in the route context so that <c>cron://</c> and <c>qtimer://</c> URIs are resolved.
    /// <example>
    /// <code>
    /// services.AddRedbRoute(route =&gt;
    /// {
    ///     route.Services.AddRedbRouteQuartz();
    ///     route.AddRouteBuilder&lt;MyRoutes&gt;();
    /// });
    /// </code>
    /// </example>
    /// </summary>
    public static IServiceCollection AddRedbRouteQuartz(this IServiceCollection services)
    {
        services.AddSingleton<CronComponent>();
        services.AddSingleton<QuartzTimerComponent>();

        // IRouteContextConfigurator is applied by RouteHostedService at startup --
        // the correct registration hook (a lazy marker singleton never fires).
        services.AddRouteContextConfigurator((sp, context) =>
        {
            context.AddComponent(sp.GetRequiredService<CronComponent>());
            context.AddComponent(sp.GetRequiredService<QuartzTimerComponent>());
        });

        return services;
    }

    /// <summary>
    /// Registers only the <see cref="CronComponent"/> in the route context
    /// so that <c>cron://</c> URIs are resolved.
    /// </summary>
    public static IServiceCollection AddRedbRouteCron(this IServiceCollection services)
    {
        services.AddRouteComponent<CronComponent>();

        return services;
    }

    /// <summary>
    /// Registers only the <see cref="QuartzTimerComponent"/> in the route context
    /// so that <c>qtimer://</c> URIs are resolved.
    /// </summary>
    public static IServiceCollection AddRedbRouteQuartzTimer(this IServiceCollection services)
    {
        services.AddRouteComponent<QuartzTimerComponent>();

        return services;
    }
}


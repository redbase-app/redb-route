using Microsoft.Extensions.DependencyInjection;
using redb.Route.Abstractions;

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

        services.AddSingleton<IQuartzComponentRegistrar>(sp =>
        {
            var context = sp.GetRequiredService<IRouteContext>();

            var cron = sp.GetRequiredService<CronComponent>();
            context.AddComponent(cron);

            var qtimer = sp.GetRequiredService<QuartzTimerComponent>();
            context.AddComponent(qtimer);

            return new QuartzComponentRegistrar();
        });

        return services;
    }

    /// <summary>
    /// Registers only the <see cref="CronComponent"/> in the route context
    /// so that <c>cron://</c> URIs are resolved.
    /// </summary>
    public static IServiceCollection AddRedbRouteCron(this IServiceCollection services)
    {
        services.AddSingleton<CronComponent>();

        services.AddSingleton<ICronComponentRegistrar>(sp =>
        {
            var context = sp.GetRequiredService<IRouteContext>();
            var component = sp.GetRequiredService<CronComponent>();
            context.AddComponent(component);
            return new CronComponentRegistrar();
        });

        return services;
    }

    /// <summary>
    /// Registers only the <see cref="QuartzTimerComponent"/> in the route context
    /// so that <c>qtimer://</c> URIs are resolved.
    /// </summary>
    public static IServiceCollection AddRedbRouteQuartzTimer(this IServiceCollection services)
    {
        services.AddSingleton<QuartzTimerComponent>();

        services.AddSingleton<IQuartzTimerComponentRegistrar>(sp =>
        {
            var context = sp.GetRequiredService<IRouteContext>();
            var component = sp.GetRequiredService<QuartzTimerComponent>();
            context.AddComponent(component);
            return new QuartzTimerComponentRegistrar();
        });

        return services;
    }
}

/// <summary>Marker interface for DI registration.</summary>
internal interface IQuartzComponentRegistrar;

/// <summary>Marker interface for DI registration.</summary>
internal interface ICronComponentRegistrar;

/// <summary>Marker interface for DI registration.</summary>
internal interface IQuartzTimerComponentRegistrar;

/// <summary>Marker registration for DI.</summary>
internal sealed class QuartzComponentRegistrar : IQuartzComponentRegistrar;

/// <summary>Marker registration for DI.</summary>
internal sealed class CronComponentRegistrar : ICronComponentRegistrar;

/// <summary>Marker registration for DI.</summary>
internal sealed class QuartzTimerComponentRegistrar : IQuartzTimerComponentRegistrar;

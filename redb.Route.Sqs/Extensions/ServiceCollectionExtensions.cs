using Microsoft.Extensions.DependencyInjection;
using redb.Route.Extensions;

namespace redb.Route.Sqs;

/// <summary>
/// Extension methods for registering the Amazon SQS + SNS transport in a DI container.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the <see cref="SqsComponent"/> (<c>sqs://</c>) and <see cref="SnsComponent"/>
    /// (<c>sns://</c>) in the route context.
    /// <example>
    /// <code>
    /// services.AddRedbRoute(route =&gt;
    /// {
    ///     route.Services.AddRedbRouteSqs();
    ///     route.AddRouteBuilder&lt;MyRoutes&gt;();
    /// });
    /// </code>
    /// </example>
    /// </summary>
    public static IServiceCollection AddRedbRouteSqs(this IServiceCollection services)
    {
        services.AddSingleton<SqsComponent>();
        services.AddSingleton<SnsComponent>();
        // IRouteContextConfigurator is applied by RouteHostedService at startup --
        // the correct registration hook (a lazy marker singleton never fires).
        services.AddRouteContextConfigurator((sp, context) =>
        {
            context.AddComponent(sp.GetRequiredService<SqsComponent>());
            context.AddComponent(sp.GetRequiredService<SnsComponent>());
        });

        return services;
    }
}


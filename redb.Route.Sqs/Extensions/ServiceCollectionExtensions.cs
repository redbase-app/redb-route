using Microsoft.Extensions.DependencyInjection;
using redb.Route.Abstractions;

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

        // Post-configure: register both components in the route context after engine start.
        services.AddSingleton<IAwsComponentRegistrar>(sp =>
        {
            var context = sp.GetRequiredService<IRouteContext>();
            context.AddComponent(sp.GetRequiredService<SqsComponent>());
            context.AddComponent(sp.GetRequiredService<SnsComponent>());
            return new AwsComponentRegistrar();
        });

        return services;
    }
}

/// <summary>Marker interface for DI registration.</summary>
internal interface IAwsComponentRegistrar;

/// <summary>Marker registration for DI.</summary>
internal sealed class AwsComponentRegistrar : IAwsComponentRegistrar;

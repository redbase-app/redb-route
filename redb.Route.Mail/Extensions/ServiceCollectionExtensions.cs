using Microsoft.Extensions.DependencyInjection;
using redb.Route.Extensions;

namespace redb.Route.Mail;

/// <summary>
/// Extension methods for registering the Mail transport (SMTP, IMAP, POP3) in a DI container.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="SmtpComponent"/>, <see cref="ImapComponent"/>, and <see cref="Pop3Component"/>
    /// so that <c>smtp://</c>, <c>imap://</c>, and <c>pop3://</c> URIs are resolved.
    /// <example>
    /// <code>
    /// services.AddRedbRoute(route =&gt;
    /// {
    ///     route.Services.AddRedbRouteMail();
    ///     route.AddRouteBuilder&lt;MyRoutes&gt;();
    /// });
    /// </code>
    /// </example>
    /// </summary>
    public static IServiceCollection AddRedbRouteMail(this IServiceCollection services)
    {
        services.AddSingleton<SmtpComponent>();
        services.AddSingleton<ImapComponent>();
        services.AddSingleton<Pop3Component>();
        // IRouteContextConfigurator is applied by RouteHostedService at startup --
        // the correct registration hook (a lazy marker singleton never fires).
        services.AddRouteContextConfigurator((sp, context) =>
        {
            context.AddComponent(sp.GetRequiredService<SmtpComponent>());
            context.AddComponent(sp.GetRequiredService<ImapComponent>());
            context.AddComponent(sp.GetRequiredService<Pop3Component>());
        });

        return services;
    }
}


using Microsoft.Extensions.DependencyInjection;
using redb.Route.Abstractions;

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

        services.AddSingleton<IMailComponentRegistrar>(sp =>
        {
            var context = sp.GetRequiredService<IRouteContext>();
            context.AddComponent(sp.GetRequiredService<SmtpComponent>());
            context.AddComponent(sp.GetRequiredService<ImapComponent>());
            context.AddComponent(sp.GetRequiredService<Pop3Component>());
            return new MailComponentRegistrar();
        });

        return services;
    }
}

/// <summary>Marker interface for DI registration.</summary>
internal interface IMailComponentRegistrar;

/// <summary>Marker registration for DI.</summary>
internal sealed class MailComponentRegistrar : IMailComponentRegistrar;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using redb.Route.Abstractions;

namespace redb.Route.Templates;

/// <summary>
/// Registers <see cref="RouteTemplateOptions"/>. Optional: without a registration the defaults apply
/// (templates relative to <see cref="AppContext.BaseDirectory"/>, Scriban syntax).
/// </summary>
public static class TemplateRegistrationExtensions
{
    /// <summary>DI registration: <c>services.AddRouteTemplates(o => o.BaseDirectory = "/etc/acme/templates")</c>.</summary>
    public static IServiceCollection AddRouteTemplates(this IServiceCollection services, Action<RouteTemplateOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var options = new RouteTemplateOptions();
        configure?.Invoke(options);
        services.TryAddSingleton(options);
        return services;
    }

    /// <summary>Registration on a context built without DI: <c>context.UseTemplates(o => o.Liquid = true)</c>.</summary>
    public static IRouteContext UseTemplates(this IRouteContext context, Action<RouteTemplateOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        var options = new RouteTemplateOptions();
        configure?.Invoke(options);
        context.AddService(typeof(RouteTemplateOptions), options);
        return context;
    }
}

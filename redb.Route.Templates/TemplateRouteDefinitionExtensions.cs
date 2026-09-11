using redb.Route.Abstractions;
using redb.Route.Core;

namespace redb.Route.Templates;

/// <summary>
/// DSL of the payload template step. A <c>string</c> argument is a <b>locator</b> (file path relative
/// to <see cref="RouteTemplateOptions.BaseDirectory"/>, or <c>assembly:Name/path</c>); inline text is
/// written as <c>TextSource.Inline("...")</c>.
/// <example>
/// <code>
/// .SetBodyTemplate("Templates/order-confirm.json.sbn", MediaType.Json)
/// .SetBodyTemplate(TextSource.Inline("""{"id": {{ headers.orderId }}}"""), MediaType.Json)
/// .SetBodyTemplate("Templates/notify.xml.sbn", MediaType.Xml, a => a
///     .Set("customer", "header.customerId")
///     .Set("total", "header.amount * header.qty")
///     .SetValue("channel", "email"))
/// .SetHeaderTemplate("X-Summary", TextSource.Inline("{{ body.items | array.size }} items"), MediaType.Text)
/// </code>
/// </example>
/// </summary>
public static class TemplateRouteDefinitionExtensions
{
    /// <summary>Renders the template into the message body; <c>ContentType</c> follows <paramref name="mediaType"/>.</summary>
    public static IRouteDefinition SetBodyTemplate(this IRouteDefinition route, string locator, MediaType mediaType, Action<TemplateArgs>? args = null)
        => SetBodyTemplate(route, TextSource.FromLocator(locator), mediaType, args);

    /// <summary>Renders the template into the message body; <c>ContentType</c> follows <paramref name="mediaType"/>.</summary>
    public static IRouteDefinition SetBodyTemplate(this IRouteDefinition route, TextSource source, MediaType mediaType, Action<TemplateArgs>? args = null)
        => Append(route, new PayloadTemplateDefinition(source, mediaType, TemplateTarget.Body, null, BuildArgs(args)));

    /// <summary>Renders the template into header <paramref name="name"/>.</summary>
    public static IRouteDefinition SetHeaderTemplate(this IRouteDefinition route, string name, string locator, MediaType mediaType, Action<TemplateArgs>? args = null)
        => SetHeaderTemplate(route, name, TextSource.FromLocator(locator), mediaType, args);

    /// <summary>Renders the template into header <paramref name="name"/>.</summary>
    public static IRouteDefinition SetHeaderTemplate(this IRouteDefinition route, string name, TextSource source, MediaType mediaType, Action<TemplateArgs>? args = null)
        => Append(route, new PayloadTemplateDefinition(source, mediaType, TemplateTarget.Header, name, BuildArgs(args)));

    /// <summary>Renders the template into exchange property <paramref name="key"/>.</summary>
    public static IRouteDefinition SetPropertyTemplate(this IRouteDefinition route, string key, string locator, MediaType mediaType, Action<TemplateArgs>? args = null)
        => SetPropertyTemplate(route, key, TextSource.FromLocator(locator), mediaType, args);

    /// <summary>Renders the template into exchange property <paramref name="key"/>.</summary>
    public static IRouteDefinition SetPropertyTemplate(this IRouteDefinition route, string key, TextSource source, MediaType mediaType, Action<TemplateArgs>? args = null)
        => Append(route, new PayloadTemplateDefinition(source, mediaType, TemplateTarget.Property, key, BuildArgs(args)));

    private static TemplateArgs? BuildArgs(Action<TemplateArgs>? configure)
    {
        if (configure is null) return null;
        var args = new TemplateArgs();
        configure(args);
        return args;
    }

    /// <summary>Appends the node to the current scope — the public tree API, identical to <c>ProcessorDefinition.AddOutput</c>.</summary>
    private static IRouteDefinition Append(IRouteDefinition route, PayloadTemplateDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(route);
        definition.Parent = route;
        route.Outputs.Add(definition);
        return route;
    }
}

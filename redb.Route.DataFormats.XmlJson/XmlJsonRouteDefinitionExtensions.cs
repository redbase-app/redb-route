using redb.Route.Abstractions;
using redb.Route.Extensions;

namespace redb.Route.DataFormats.XmlJson;

/// <summary>DSL and registration sugar for <see cref="XmlJsonDataFormat"/>.</summary>
public static class XmlJsonRouteDefinitionExtensions
{
    /// <summary>Turns an XML body into JSON, with per-node options.</summary>
    public static IRouteDefinition MarshalXmlJson(this IRouteDefinition route, Action<XmlJsonOptions>? configure = null)
        => route.Marshal(new XmlJsonDataFormat(Build(configure)));

    /// <summary>
    /// Turns a JSON body into XML as <typeparamref name="T"/> (<c>string</c>, <c>byte[]</c>, <c>XDocument</c>,
    /// <c>XElement</c>, <c>XmlDocument</c>), or an XML body into the JSON tree for <c>T</c> = <c>JsonNode</c>.
    /// </summary>
    public static IRouteDefinition UnmarshalXmlJson<T>(this IRouteDefinition route, Action<XmlJsonOptions>? configure = null)
        => route.Unmarshal<T>(new XmlJsonDataFormat(Build(configure)));

    /// <summary>
    /// Registers the format on the context under its content type and the name <c>xmljson</c>, for
    /// <c>&lt;marshal format="xmljson"/&gt;</c> and <c>Marshal("xmljson")</c>.
    /// </summary>
    public static IRouteContext AddXmlJsonDataFormat(this IRouteContext context, Action<XmlJsonOptions>? configure = null)
        => context.AddDataFormat(new XmlJsonDataFormat(Build(configure)), XmlJsonDataFormat.Name);

    /// <summary>DI form of <see cref="AddXmlJsonDataFormat(IRouteContext, Action{XmlJsonOptions})"/>.</summary>
    public static RedbRouteBuilder AddXmlJsonDataFormat(this RedbRouteBuilder builder, Action<XmlJsonOptions>? configure = null)
        => builder.AddDataFormat(new XmlJsonDataFormat(Build(configure)), XmlJsonDataFormat.Name);

    private static XmlJsonOptions Build(Action<XmlJsonOptions>? configure)
    {
        var options = new XmlJsonOptions();
        configure?.Invoke(options);
        return options;
    }
}

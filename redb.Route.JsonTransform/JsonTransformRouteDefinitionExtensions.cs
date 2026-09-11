using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using redb.Route.Abstractions;
using redb.Route.Core;

namespace redb.Route.JsonTransform;

/// <summary>
/// DSL of the JSON transform step. A <c>string</c> argument is a <b>locator</b> (file relative to
/// <see cref="JsonTransformOptions.BaseDirectory"/>, or <c>assembly:Name/path</c>); an inline
/// specification is <c>TextSource.Inline("...")</c>.
/// <example>
/// <code>
/// .TransformJson("Transforms/order-to-shipment.jsonata")
/// .TransformJson(TextSource.Inline("{ 'id': orderId, 'to': { 'city': address.city }, 'tenant': $headers.tenant }"))
/// .TransformJson("Transforms/x.jsonata", JsonTransformOutput.Node)
/// </code>
/// </example>
/// </summary>
public static class JsonTransformRouteDefinitionExtensions
{
    /// <summary>Applies the JSONata specification at <paramref name="locator"/> to the JSON body.</summary>
    public static IRouteDefinition TransformJson(this IRouteDefinition route, string locator, JsonTransformOutput output = JsonTransformOutput.String)
        => TransformJson(route, TextSource.FromLocator(locator), output);

    /// <summary>Applies the JSONata specification to the JSON body.</summary>
    public static IRouteDefinition TransformJson(this IRouteDefinition route, TextSource specification, JsonTransformOutput output = JsonTransformOutput.String)
    {
        ArgumentNullException.ThrowIfNull(route);
        var definition = new JsonTransformDefinition(specification, output) { Parent = route };
        route.Outputs.Add(definition);   // the public tree API, identical to ProcessorDefinition.AddOutput
        return route;
    }

    /// <summary>DI registration: <c>services.AddJsonTransform(o => o.BaseDirectory = "/etc/acme/transforms")</c>.</summary>
    public static IServiceCollection AddJsonTransform(this IServiceCollection services, Action<JsonTransformOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var options = new JsonTransformOptions();
        configure?.Invoke(options);
        services.TryAddSingleton(options);
        return services;
    }

    /// <summary>Registration on a context built without DI: <c>context.UseJsonTransform(o => o.Indent = true)</c>.</summary>
    public static IRouteContext UseJsonTransform(this IRouteContext context, Action<JsonTransformOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        var options = new JsonTransformOptions();
        configure?.Invoke(options);
        context.AddService(typeof(JsonTransformOptions), options);
        return context;
    }
}

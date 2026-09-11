using redb.Route.Core;

namespace redb.Route.Http.Rest;

/// <summary>Entry point of the REST DSL: <c>this.Rest("/api/orders", o => o.Port = 8080)</c> inside a <see cref="RouteBuilder"/>.</summary>
public static class RestRouteBuilderExtensions
{
    /// <summary>
    /// Starts a REST declaration under <paramref name="basePath"/>. Each verb finished with <c>To(...)</c>
    /// or <c>Route()</c> becomes an HTTP route on the shared Kestrel host; an OpenAPI document is served
    /// at <see cref="RestOptions.OpenApiPath"/> (default <c>{basePath}/openapi.json</c>).
    /// </summary>
    public static RestDefinition Rest(this RouteBuilder builder, string basePath, Action<RestOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(basePath);
        var options = new RestOptions();
        configure?.Invoke(options);
        return new RestDefinition(builder, basePath, options);
    }
}

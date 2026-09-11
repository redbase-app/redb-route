using redb.Route.Abstractions;

namespace redb.Route.Http.Rest;

/// <summary>
/// One operation of a <see cref="RestDefinition"/>: method + path template, media types, binding
/// types and description. Finished with <see cref="To"/> (send to an endpoint) or <see cref="Route"/>
/// (write the steps inline); both create the underlying HTTP route.
/// </summary>
public sealed class RestVerbDefinition
{
    private readonly RestDefinition _rest;

    internal RestVerbDefinition(RestDefinition rest, string method, string path)
    {
        _rest = rest;
        Method = method;
        Path = path;
    }

    /// <summary>HTTP method (<c>GET</c>, <c>POST</c>, …).</summary>
    public string Method { get; }

    /// <summary>Path template relative to the <see cref="RestDefinition.BasePath"/> (<c>/{id}</c>, <c>/{id}/status</c>, or empty).</summary>
    public string Path { get; }

    /// <summary>Full path template (<c>/api/orders/{id}</c>).</summary>
    public string FullPath => RestDefinition.JoinPath(_rest.BasePath, Path);

    /// <summary>Expected request media type; a request with another <c>Content-Type</c> is answered with 415.</summary>
    public string? ConsumesType { get; private set; }

    /// <summary>Response media type written to the reply.</summary>
    public string? ProducesType { get; private set; }

    /// <summary>Binding mode override for this verb.</summary>
    public RestBindingMode? Binding { get; private set; }

    /// <summary>CLR type of the request body (JSON binding and OpenAPI).</summary>
    public Type? RequestType { get; private set; }

    /// <summary>CLR type of the response body (OpenAPI).</summary>
    public Type? ResponseType { get; private set; }

    /// <summary>Human description → OpenAPI <c>summary</c>.</summary>
    public string? Summary { get; private set; }

    /// <summary>Route id; default <c>rest:METHOD /full/path</c>. Also the OpenAPI <c>operationId</c>.</summary>
    public string? RouteId { get; private set; }

    /// <summary>Target endpoint set by <see cref="To"/>; <c>null</c> for inline routes.</summary>
    public string? Target { get; private set; }

    /// <summary>Path parameter names in declaration order (<c>{id}</c> → <c>id</c>).</summary>
    public IReadOnlyList<string> PathParameters => RestDefinition.PathParameterNames(FullPath);

    /// <summary>Request media type this operation accepts.</summary>
    public RestVerbDefinition Consumes(string contentType) { ConsumesType = Required(contentType); return this; }

    /// <summary>Response media type this operation produces.</summary>
    public RestVerbDefinition Produces(string contentType) { ProducesType = Required(contentType); return this; }

    /// <summary>Binding mode for this operation.</summary>
    public RestVerbDefinition BindingMode(RestBindingMode mode) { Binding = mode; return this; }

    /// <summary>Request body type: unmarshalled under JSON binding, described in OpenAPI.</summary>
    public RestVerbDefinition Type<T>() { RequestType = typeof(T); return this; }

    /// <summary>Response body type, described in OpenAPI.</summary>
    public RestVerbDefinition OutType<T>() { ResponseType = typeof(T); return this; }

    /// <summary>Human description (OpenAPI <c>summary</c>).</summary>
    public RestVerbDefinition Description(string summary) { Summary = Required(summary); return this; }

    /// <summary>Route id / OpenAPI <c>operationId</c>.</summary>
    public RestVerbDefinition Id(string routeId) { RouteId = Required(routeId); return this; }

    /// <summary>Sends the request to <paramref name="uri"/> (usually <c>direct:...</c>) and returns to the REST declaration for the next verb.</summary>
    public RestDefinition To(string uri)
    {
        Target = Required(uri);
        _rest.Complete(this);
        return _rest;
    }

    /// <summary>Handles the request with steps written inline; returns the route to add them to.</summary>
    public IRouteDefinition Route() => _rest.Complete(this);

    internal RestBindingMode EffectiveBinding(RestOptions options) => Binding ?? options.BindingMode;
    internal string? EffectiveConsumes(RestOptions options) => ConsumesType ?? options.Consumes;
    internal string? EffectiveProduces(RestOptions options) => ProducesType ?? options.Produces;

    private static string Required(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return value;
    }
}

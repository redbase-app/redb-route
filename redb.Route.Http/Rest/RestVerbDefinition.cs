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

    /// <summary>Parameters declared with <see cref="Param"/>, in declaration order.</summary>
    public IReadOnlyList<RestParamDefinition> Parameters => _parameters;

    /// <summary>Client request validation override for this verb; <c>null</c> = the declaration's option.</summary>
    public bool? Validation { get; private set; }

    private readonly List<RestParamDefinition> _parameters = [];

    /// <summary>
    /// Declares a parameter: described in OpenAPI, and when client request validation is on, a missing
    /// required one or a value that does not convert to <paramref name="dataType"/> is answered with 400.
    /// A path parameter must be a segment of the template and is always required, so
    /// <paramref name="required"/> left <c>null</c> means required for a path parameter and optional
    /// for the others.
    /// </summary>
    public RestVerbDefinition Param(string name, RestParamType type = RestParamType.Query, bool? required = null,
        RestParamDataType dataType = RestParamDataType.String, string? description = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (type == RestParamType.Path)
        {
            if (!PathParameters.Contains(name, StringComparer.Ordinal))
                throw new ArgumentException(
                    $"Path parameter '{name}' is not a segment of '{FullPath}'.", nameof(name));
            if (required == false)
                throw new ArgumentException(
                    $"Path parameter '{name}' cannot be optional: a path parameter is always required.", nameof(required));
        }
        var comparer = type == RestParamType.Header ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        if (_parameters.Any(p => p.Type == type && comparer.Equals(p.Name, name)))
            throw new ArgumentException($"{type} parameter '{name}' is declared twice on {Method} {FullPath}.", nameof(name));

        _parameters.Add(new RestParamDefinition(name, type, required ?? type == RestParamType.Path, dataType, description));
        return this;
    }

    /// <summary>Switches client request validation on or off for this verb (see <see cref="RestOptions.ClientRequestValidation"/>).</summary>
    public RestVerbDefinition ClientRequestValidation(bool enabled = true) { Validation = enabled; return this; }

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
    internal bool EffectiveValidation(RestOptions options) => Validation ?? options.ClientRequestValidation;

    private static string Required(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return value;
    }
}

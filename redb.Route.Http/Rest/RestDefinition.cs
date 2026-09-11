using System.Text.RegularExpressions;
using redb.Route.Abstractions;
using redb.Route.Core;

namespace redb.Route.Http.Rest;

/// <summary>
/// REST DSL (Apache Camel <c>rest()</c> parity) on top of the HTTP consumer and the shared Kestrel host:
/// every verb becomes an ordinary route <c>From("http://host:port/base/path?methods=GET&amp;inOut=true")</c>,
/// so path templates, method dispatch (405), CORS, TLS and everything else stay where they already are.
/// The DSL adds the declarative layer: path parameters as <c>header.id</c>, query as <c>header.query.page</c>,
/// <c>Consumes</c> / <c>Produces</c> with 415 on mismatch, JSON binding to CLR types, and an OpenAPI 3.0
/// document generated from the declarations.
/// <example>
/// <code>
/// this.Rest("/api/orders", o => o.Port = 8080)
///     .Get("/{id}").To("direct:get-order")
///     .Post().Consumes("application/json").Type&lt;Order&gt;().To("direct:create-order")
///     .Put("/{id}/status").To("direct:set-status")
///     .Delete("/{id}").To("direct:cancel-order");
/// </code>
/// </example>
/// </summary>
public sealed class RestDefinition
{
    private static readonly Regex PathParameter = new(@"\{(\w+)\}", RegexOptions.Compiled);
    private readonly RouteBuilder _builder;
    private readonly List<RestVerbDefinition> _verbs = [];
    private bool _openApiRegistered;

    internal RestDefinition(RouteBuilder builder, string basePath, RestOptions options)
    {
        _builder = builder;
        BasePath = "/" + basePath.Trim('/');
        Options = options;
    }

    /// <summary>Common path prefix of the verbs (<c>/api/orders</c>).</summary>
    public string BasePath { get; }

    /// <summary>Settings of this declaration.</summary>
    public RestOptions Options { get; }

    /// <summary>Verbs declared so far (completed with <c>To</c> / <c>Route</c>).</summary>
    public IReadOnlyList<RestVerbDefinition> Verbs => _verbs;

    /// <summary>Declares a GET operation.</summary>
    public RestVerbDefinition Get(string path = "") => new(this, "GET", path);

    /// <summary>Declares a POST operation.</summary>
    public RestVerbDefinition Post(string path = "") => new(this, "POST", path);

    /// <summary>Declares a PUT operation.</summary>
    public RestVerbDefinition Put(string path = "") => new(this, "PUT", path);

    /// <summary>Declares a DELETE operation.</summary>
    public RestVerbDefinition Delete(string path = "") => new(this, "DELETE", path);

    /// <summary>Declares a PATCH operation.</summary>
    public RestVerbDefinition Patch(string path = "") => new(this, "PATCH", path);

    /// <summary>Declares a HEAD operation.</summary>
    public RestVerbDefinition Head(string path = "") => new(this, "HEAD", path);

    /// <summary>Creates the HTTP route of a finished verb; returns the route so inline steps can follow.</summary>
    internal IRouteDefinition Complete(RestVerbDefinition verb)
    {
        _verbs.Add(verb);
        EnsureOpenApiRoute();

        var routeId = verb.RouteId ?? $"rest:{verb.Method} {verb.FullPath}";
        var route = _builder.From(ConsumerUri(verb.FullPath, verb.Method)).RouteId(routeId);
        Append(route, new RestRequestDefinition(verb, Options));

        if (verb.Target is not null)
        {
            route.To(verb.Target);
            Append(route, new RestResponseDefinition(verb, Options));
            return route;
        }

        // Inline steps live in their own direct route, so the response step can still run after them.
        // The name carries a fingerprint of the full route id: sanitizing alone maps "/{id}" and "/id"
        // to the same text, and a direct endpoint registered twice silently keeps the last handler.
        var inner = $"direct://rest-{Sanitize(routeId)}-{Fingerprint(routeId)}";
        route.To(inner);
        Append(route, new RestResponseDefinition(verb, Options));
        return _builder.From(inner).RouteId(routeId + " (handler)");
    }

    /// <summary>Path the OpenAPI document is served at (<see cref="RestOptions.OpenApiPath"/> or <c>{basePath}/openapi.json</c>).</summary>
    public string OpenApiPath => Options.OpenApiPath ?? JoinPath(BasePath, "openapi.json");

    private void EnsureOpenApiRoute()
    {
        if (_openApiRegistered || !Options.OpenApi) return;
        _openApiRegistered = true;

        var route = _builder.From(ConsumerUri(OpenApiPath, "GET")).RouteId($"rest:openapi {BasePath}");
        Append(route, new OpenApiDocumentDefinition(this));
    }

    private string ConsumerUri(string path, string method)
    {
        var uri = $"http://{Options.Host}:{Options.Port}{path}?methods={method}&inOut=true";
        return string.IsNullOrWhiteSpace(Options.ExtraConsumerOptions) ? uri : uri + "&" + Options.ExtraConsumerOptions.TrimStart('&');
    }

    private static void Append(IRouteDefinition route, IProcessorDefinition step)
    {
        step.Parent = route;
        route.Outputs.Add(step);
    }

    private static string Sanitize(string routeId)
        => Regex.Replace(routeId, @"[^A-Za-z0-9_.-]+", "-").Trim('-');

    /// <summary>Eight hex digits of the route id's SHA-256: stable across processes, distinct for ids that sanitize alike.</summary>
    private static string Fingerprint(string routeId)
        => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(routeId)))[..8].ToLowerInvariant();

    internal static string JoinPath(string basePath, string path)
    {
        var tail = path.Trim('/');
        return tail.Length == 0 ? basePath : $"{basePath.TrimEnd('/')}/{tail}";
    }

    internal static IReadOnlyList<string> PathParameterNames(string template)
        => PathParameter.Matches(template).Select(m => m.Groups[1].Value).ToList();
}

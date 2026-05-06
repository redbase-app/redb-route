using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using redb.Route.Abstractions;

namespace redb.Route.Controllers;

/// <summary>
/// Dispatches HTTP requests to controller methods using REST conventions.
/// Reads <c>redbHttp.Method</c> and <c>redbHttp.Path</c> headers set by the HTTP consumer,
/// resolves controller actions from the <see cref="ControllerRegistry"/>,
/// and binds parameters from route templates, query strings, and request body.
/// <para>
/// This is the HTTP-native equivalent of <see cref="ControllerDispatcherProcessor"/>
/// that works directly with the headers produced by the HTTP consumer —
/// no manual header translation required.
/// </para>
/// </summary>
public sealed class HttpControllerDispatcher : IProcessor
{
    private readonly ControllerRegistry _registry;
    private readonly IRouteContext _context;
    private readonly JsonSerializerOptions _jsonOptions;

    // Headers set by HttpConsumer (string constants to avoid dependency on redb.Route.Http)
    internal const string HttpMethodHeader = "redbHttp.Method";
    internal const string HttpPathHeader = "redbHttp.Path";
    internal const string RouteParamPrefix = "redbHttp.RouteParam.";
    internal const string QueryParamPrefix = "redbHttp.QueryParam.";

    /// <summary>
    /// Default JSON serializer options used when no custom options are supplied.
    /// camelCase property naming matches the de-facto HTTP API convention
    /// (Google/Microsoft REST style guides, JSON:API, ASP.NET Core default) and
    /// stays consistent with <see cref="ControllerDispatcherProcessor"/>.
    /// <para>
    /// Uses <see cref="JavaScriptEncoder.UnsafeRelaxedJsonEscaping"/> so non-ASCII
    /// (Cyrillic, emoji, diacritics) and ASCII punctuation like <c>"</c> are emitted
    /// as-is in UTF-8 instead of being escaped to <c>\u0022</c>/<c>\u0410</c>. This is
    /// safe for HTTP API responses; it's only unsafe when embedding JSON directly
    /// inside HTML/JS, which dispatcher output never does.
    /// </para>
    /// </summary>
    private static readonly JsonSerializerOptions DefaultJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <param name="registry">Controller registry with registered actions.</param>
    /// <param name="context">Route context for controller instantiation.</param>
    /// <param name="jsonOptions">
    /// Optional JSON serializer options used for body (de)serialization and the dispatcher's
    /// own error envelope. When <c>null</c>, camelCase defaults are used. Pass a custom
    /// instance to override naming policy / converters / etc. for a given mount point.
    /// </param>
    public HttpControllerDispatcher(
        ControllerRegistry registry,
        IRouteContext context,
        JsonSerializerOptions? jsonOptions = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _jsonOptions = jsonOptions ?? DefaultJsonOptions;
    }

    /// <inheritdoc />
    public async Task Process(IExchange exchange, CancellationToken ct = default)
    {
        var method = exchange.In.GetHeader<string>(HttpMethodHeader);
        var path = exchange.In.GetHeader<string>(HttpPathHeader);

        if (string.IsNullOrEmpty(method) || string.IsNullOrEmpty(path))
        {
            WriteError(exchange, 400, "BadRequest",
                $"Missing required headers: {HttpMethodHeader} and/or {HttpPathHeader}");
            return;
        }

        // Strip leading slash — registry templates don't use it (e.g. "modules/42", not "/modules/42")
        var normalizedPath = path.StartsWith('/') ? path[1..] : path;

        var action = _registry.Resolve(method, normalizedPath, out var routeParams);
        if (action is null)
        {
            WriteError(exchange, 404, "NotFound", $"No action matches {method} {path}");
            return;
        }

        // Merge redbHttp.RouteParam.* headers into routeParams (consumer extracts these from templates)
        MergeHttpRouteParams(exchange, routeParams);

        try
        {
            var controller = (RedbController)Activator.CreateInstance(action.ControllerType)!;
            controller.Context = _context;
            controller.Exchange = exchange;

            var parameters = ResolveHttpParameters(action.Method, exchange, routeParams);
            var result = action.Method.Invoke(controller, parameters);

            if (result is Task task)
            {
                await task;
                result = GetTaskResult(task);
            }

            WriteResult(exchange, result);
        }
        catch (TargetInvocationException tie) when (tie.InnerException is not null)
        {
            // Sync controller methods surface user exceptions wrapped in TIE via MethodInfo.Invoke.
            // Async methods return a faulted Task; `await` re-throws the original exception (no TIE).
            // Only unwrap TIE — never blindly deref .InnerException on arbitrary exceptions
            // (that would skip a level and misclassify async errors whose first InnerException
            // is a deeper transient wrapper like SocketException inside a DbException).
            WriteError(exchange, 500, "InternalError", tie.InnerException.Message);
        }
        catch (Exception ex)
        {
            WriteError(exchange, 500, "InternalError", ex.Message);
        }
    }

    /// <summary>
    /// Resolves parameters using HTTP-native headers:
    /// route params from registry + redbHttp.RouteParam.*, query from redbHttp.QueryParam.*,
    /// body from byte[] (HTTP consumer stores raw bytes).
    /// </summary>
    private object?[] ResolveHttpParameters(
        MethodInfo method, IExchange exchange, IReadOnlyDictionary<string, string> routeParams)
    {
        var parameters = method.GetParameters();
        var values = new object?[parameters.Length];

        for (var i = 0; i < parameters.Length; i++)
        {
            values[i] = ResolveHttpParameter(parameters[i], exchange, routeParams);
        }

        return values;
    }

    private object? ResolveHttpParameter(
        ParameterInfo param, IExchange exchange, IReadOnlyDictionary<string, string> routeParams)
    {
        if (param.ParameterType == typeof(CancellationToken))
            return CancellationToken.None;

        // [FromBody]
        if (param.GetCustomAttribute<Attributes.FromBodyAttribute>() is not null)
            return ResolveFromBody(exchange, param.ParameterType);

        // [FromHeader("name")]
        if (param.GetCustomAttribute<Attributes.FromHeaderAttribute>() is { } headerAttr)
        {
            var raw = exchange.In.getHeader(headerAttr.Name);
            if (raw is not null) return ParameterResolver.ConvertValue(raw, param.ParameterType);
            return param.HasDefaultValue ? param.DefaultValue : ParameterResolver.ConvertValue(null, param.ParameterType);
        }

        // [FromProperty("name")]
        if (param.GetCustomAttribute<Attributes.FromPropertyAttribute>() is { } propAttr)
        {
            var raw = exchange.getProperty(propAttr.Name);
            if (raw is not null) return ParameterResolver.ConvertValue(raw, param.ParameterType);
            return param.HasDefaultValue ? param.DefaultValue : ParameterResolver.ConvertValue(null, param.ParameterType);
        }

        // [FromQuery("name")] — reads from redbHttp.QueryParam.{name}
        if (param.GetCustomAttribute<Attributes.FromQueryAttribute>() is { } queryAttr)
        {
            var raw = exchange.In.getHeader($"{QueryParamPrefix}{queryAttr.Name}");
            if (raw is not null) return ParameterResolver.ConvertValue(raw, param.ParameterType);
            return param.HasDefaultValue ? param.DefaultValue : ParameterResolver.ConvertValue(null, param.ParameterType);
        }

        // [FromRoute("name")] — reads from route params extracted by template matching
        if (param.GetCustomAttribute<Attributes.FromRouteAttribute>() is { } routeAttr)
        {
            routeParams.TryGetValue(routeAttr.Name, out var routeValue);
            if (routeValue is not null) return ParameterResolver.ConvertValue(routeValue, param.ParameterType);
            return param.HasDefaultValue ? param.DefaultValue : ParameterResolver.ConvertValue(null, param.ParameterType);
        }

        // No attribute — try route params by name, then body for complex types
        if (routeParams.TryGetValue(param.Name!, out var implicitRouteValue))
            return ParameterResolver.ConvertValue(implicitRouteValue, param.ParameterType);

        if (!IsSimpleType(param.ParameterType))
            return ResolveFromBody(exchange, param.ParameterType);

        return param.HasDefaultValue ? param.DefaultValue : null;
    }

    /// <summary>
    /// Deserializes the body from the exchange. HTTP consumer stores body as byte[],
    /// so we handle JSON deserialization from bytes, string, or typed object.
    /// </summary>
    private object? ResolveFromBody(IExchange exchange, Type targetType)
    {
        var body = exchange.In.Body;
        if (body is null) return null;

        if (targetType.IsInstanceOfType(body))
            return body;

        // HTTP consumer stores body as byte[] — deserialize JSON from bytes
        if (body is byte[] bytes)
        {
            if (bytes.Length == 0) return null;
            if (targetType == typeof(byte[])) return bytes;
            if (targetType == typeof(string)) return System.Text.Encoding.UTF8.GetString(bytes);
            return JsonSerializer.Deserialize(bytes, targetType, _jsonOptions);
        }

        if (body is string json)
            return JsonSerializer.Deserialize(json, targetType, _jsonOptions);

        if (body is JsonElement element)
            return element.Deserialize(targetType, _jsonOptions);

        return ParameterResolver.ConvertValue(body, targetType);
    }

    private static void MergeHttpRouteParams(IExchange exchange, Dictionary<string, string> routeParams)
    {
        foreach (var header in exchange.In.Headers)
        {
            if (header.Key.StartsWith(RouteParamPrefix, StringComparison.Ordinal)
                && header.Value is not null)
            {
                var paramName = header.Key[RouteParamPrefix.Length..];
                routeParams.TryAdd(paramName, header.Value.ToString()!);
            }
        }
    }

    // HTTP response code header read by HttpConsumer (string constant to avoid dependency on redb.Route.Http)
    internal const string HttpResponseCodeHeader = "redbHttp.ResponseCode";

    private void WriteResult(IExchange exchange, object? result)
    {
        exchange.Out ??= exchange.In.Clone();
        var defaultCode = result is null ? 204 : 200;

        if (result is not null)
        {
            // Serialize to JSON bytes for HTTP transport (consumer writes byte[] directly)
            if (result is not byte[] and not string)
                result = JsonSerializer.SerializeToUtf8Bytes(result, _jsonOptions);
            exchange.Out.Body = result;
        }

        // Respect response meta already set by the controller (e.g. via facade Forward
        // that propagated an inner OnException 5xx). Dispatcher only fills in defaults.
        if (!exchange.Out.Headers.ContainsKey("status.code"))
            exchange.Out.setHeader("status.code", defaultCode);
        if (!exchange.Out.Headers.ContainsKey(HttpResponseCodeHeader))
            exchange.Out.setHeader(HttpResponseCodeHeader, defaultCode);
        if (result is not null && !exchange.Out.Headers.ContainsKey("Content-Type"))
            exchange.Out.setHeader("Content-Type", "application/json");
    }

    private void WriteError(IExchange exchange, int statusCode, string error, string message)
    {
        exchange.Out ??= exchange.In.Clone();
        exchange.Out.Body = JsonSerializer.SerializeToUtf8Bytes(
            new ControllerErrorResponse
            {
                Error = error,
                Message = message,
                StatusCode = statusCode
            }, _jsonOptions);
        // Errors are authoritative: overwrite whatever a controller may have set before throwing.
        exchange.Out.setHeader("status.code", statusCode);
        exchange.Out.setHeader(HttpResponseCodeHeader, statusCode);
        exchange.Out.setHeader("Content-Type", "application/json");
    }

    private static object? GetTaskResult(Task task)
    {
        var type = task.GetType();
        if (!type.IsGenericType) return null;
        return type.GetProperty("Result")?.GetValue(task);
    }

    private static bool IsSimpleType(Type type)
    {
        var t = Nullable.GetUnderlyingType(type) ?? type;
        return t.IsPrimitive || t == typeof(string) || t == typeof(decimal)
            || t == typeof(DateTime) || t == typeof(DateTimeOffset)
            || t == typeof(Guid) || t.IsEnum;
    }
}

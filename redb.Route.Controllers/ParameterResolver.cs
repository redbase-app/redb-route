using System.Reflection;
using System.Text.Json;
using redb.Route.Abstractions;
using redb.Route.Controllers.Attributes;

namespace redb.Route.Controllers;

/// <summary>
/// Resolves method parameters from <see cref="IExchange"/> based on binding attributes.
/// Handles JSON deserialization for [FromBody], primitive conversion for others.
/// </summary>
public static class ParameterResolver
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// Resolves all parameters for a controller method from the exchange and route params.
    /// </summary>
    /// <param name="method">Target method.</param>
    /// <param name="exchange">Current exchange.</param>
    /// <param name="routeParams">Route path parameters extracted by template matching.</param>
    /// <returns>Array of resolved parameter values.</returns>
    public static object?[] ResolveParameters(
        MethodInfo method,
        IExchange exchange,
        IReadOnlyDictionary<string, string> routeParams)
    {
        var parameters = method.GetParameters();
        var values = new object?[parameters.Length];

        for (var i = 0; i < parameters.Length; i++)
        {
            values[i] = ResolveParameter(parameters[i], exchange, routeParams);
        }

        return values;
    }

    private static object? ResolveParameter(
        ParameterInfo param,
        IExchange exchange,
        IReadOnlyDictionary<string, string> routeParams)
    {
        // CancellationToken — special case
        if (param.ParameterType == typeof(CancellationToken))
            return CancellationToken.None;

        // [FromBody]
        if (param.GetCustomAttribute<FromBodyAttribute>() is not null)
            return ResolveFromBody(exchange, param.ParameterType);

        // [FromHeader("name")]
        if (param.GetCustomAttribute<FromHeaderAttribute>() is { } headerAttr)
        {
            var raw = exchange.In.getHeader(headerAttr.Name);
            if (raw is not null) return ConvertValue(raw, param.ParameterType);
            return param.HasDefaultValue ? param.DefaultValue : ConvertValue(null, param.ParameterType);
        }

        // [FromProperty("name")]
        if (param.GetCustomAttribute<FromPropertyAttribute>() is { } propAttr)
        {
            var raw = exchange.getProperty(propAttr.Name);
            if (raw is not null) return ConvertValue(raw, param.ParameterType);
            return param.HasDefaultValue ? param.DefaultValue : ConvertValue(null, param.ParameterType);
        }

        // [FromQuery("name")]
        if (param.GetCustomAttribute<FromQueryAttribute>() is { } queryAttr)
        {
            var raw = exchange.In.getHeader($"query.{queryAttr.Name}");
            if (raw is not null) return ConvertValue(raw, param.ParameterType);
            return param.HasDefaultValue ? param.DefaultValue : ConvertValue(null, param.ParameterType);
        }

        // [FromRoute("name")]
        if (param.GetCustomAttribute<FromRouteAttribute>() is { } routeAttr)
        {
            routeParams.TryGetValue(routeAttr.Name, out var routeValue);
            if (routeValue is not null) return ConvertValue(routeValue, param.ParameterType);
            return param.HasDefaultValue ? param.DefaultValue : ConvertValue(null, param.ParameterType);
        }

        // No attribute — try route params by parameter name, then body
        if (routeParams.TryGetValue(param.Name!, out var implicitRouteValue))
            return ConvertValue(implicitRouteValue, param.ParameterType);

        // Default: try to resolve from body if it's a complex type
        if (!IsSimpleType(param.ParameterType))
            return ResolveFromBody(exchange, param.ParameterType);

        return param.HasDefaultValue ? param.DefaultValue : null;
    }

    private static object? ResolveFromBody(IExchange exchange, Type targetType)
    {
        var body = exchange.In.Body;
        if (body is null)
            return null;

        if (targetType.IsInstanceOfType(body))
            return body;

        // HTTP consumer stores body as byte[] — deserialize JSON from bytes
        if (body is byte[] bytes)
        {
            if (bytes.Length == 0) return null;
            if (targetType == typeof(byte[])) return bytes;
            if (targetType == typeof(string)) return System.Text.Encoding.UTF8.GetString(bytes);
            return JsonSerializer.Deserialize(bytes, targetType, JsonOptions);
        }

        // If body is a string, try JSON deserialization
        if (body is string json)
            return JsonSerializer.Deserialize(json, targetType, JsonOptions);

        // If body is a JsonElement
        if (body is JsonElement element)
            return element.Deserialize(targetType, JsonOptions);

        return ConvertValue(body, targetType);
    }

    internal static object? ConvertValue(object? value, Type targetType)
    {
        if (value is null)
            return targetType.IsValueType ? Activator.CreateInstance(targetType) : null;

        if (targetType.IsInstanceOfType(value))
            return value;

        var underlying = Nullable.GetUnderlyingType(targetType) ?? targetType;

        if (underlying == typeof(Guid) && value is string gs)
            return Guid.Parse(gs);

        if (underlying.IsEnum && value is string es)
            return Enum.Parse(underlying, es, ignoreCase: true);

        try
        {
            return Convert.ChangeType(value, underlying);
        }
        catch
        {
            return targetType.IsValueType ? Activator.CreateInstance(targetType) : null;
        }
    }

    /// <summary>
    /// Resolves method parameters positionally from a SignalR-style body.
    /// Body is unpacked as: null → no args, single value → [value], object[] → positional.
    /// </summary>
    /// <param name="method">Target method.</param>
    /// <param name="body">Exchange body (null, single value, or object[] from RedbBridgeHub).</param>
    /// <param name="ct">Cancellation token to inject for CancellationToken parameters.</param>
    /// <returns>Array of resolved parameter values.</returns>
    internal static object?[] ResolvePositional(MethodInfo method, object? body, CancellationToken ct = default)
    {
        var allParams = method.GetParameters();
        if (allParams.Length == 0)
            return [];

        // A JSON object binds BY NAME (honouring [FromRoute]/[FromQuery] key names, [FromBody] = the whole
        // object, a lone unbound complex parameter = the whole object). Arrays and everything else stay
        // positional, so existing single-value and positional-array callers are unchanged.
        if (body is JsonElement { ValueKind: JsonValueKind.Object } jsonObject)
            return ResolveNamed(allParams, jsonObject, ct);

        // Unpack body into args array
        var args = body switch
        {
            null => Array.Empty<object?>(),
            object[] arr => arr,
            _ => new[] { body }
        };

        var values = new object?[allParams.Length];
        var argIdx = 0;

        for (var i = 0; i < allParams.Length; i++)
        {
            var param = allParams[i];

            if (param.ParameterType == typeof(CancellationToken))
            {
                values[i] = ct;
                continue;
            }

            if (argIdx < args.Length)
            {
                values[i] = ConvertOrDeserialize(args[argIdx], param.ParameterType);
                argIdx++;
            }
            else
            {
                values[i] = param.HasDefaultValue ? param.DefaultValue : null;
            }
        }

        return values;
    }

    /// <summary>
    /// Binds a JSON-object body by name. A <c>[FromBody]</c> parameter (or, when none is present, the single
    /// unbound complex parameter) receives the whole object; every other parameter takes the object property
    /// matching its name — or its <c>[FromRoute]</c> / <c>[FromQuery]</c> key — case-insensitively; unmatched
    /// parameters fall back to their default. <see cref="CancellationToken"/> is injected. Extra object keys
    /// are ignored by deserialization.
    /// </summary>
    private static object?[] ResolveNamed(ParameterInfo[] parameters, JsonElement obj, CancellationToken ct)
    {
        var hasFromBody = false;
        foreach (var p in parameters)
            if (p.GetCustomAttribute<FromBodyAttribute>() is not null) { hasFromBody = true; break; }

        var values = new object?[parameters.Length];
        var loneComplex = new List<int>();

        for (var i = 0; i < parameters.Length; i++)
        {
            var p = parameters[i];

            if (p.ParameterType == typeof(CancellationToken)) { values[i] = ct; continue; }

            if (p.GetCustomAttribute<FromBodyAttribute>() is not null)
            {
                values[i] = obj.Deserialize(p.ParameterType, JsonOptions);
                continue;
            }

            var key = p.GetCustomAttribute<FromRouteAttribute>()?.Name
                      ?? p.GetCustomAttribute<FromQueryAttribute>()?.Name
                      ?? p.Name!;

            if (TryGetPropertyIgnoreCase(obj, key, out var element))
            {
                values[i] = ConvertOrDeserialize(element, p.ParameterType);
                continue;
            }

            // Unbound: take the default now; a lone complex parameter may receive the whole object below.
            values[i] = p.HasDefaultValue ? p.DefaultValue : ConvertValue(null, p.ParameterType);
            if (!hasFromBody && !IsSimpleType(p.ParameterType))
                loneComplex.Add(i);
        }

        // No [FromBody], exactly one unbound complex parameter: it receives the whole object. This preserves
        // the common `Method(RequestDto dto)` shape whose parameter name never matches the object's keys.
        if (!hasFromBody && loneComplex.Count == 1)
            values[loneComplex[0]] = obj.Deserialize(parameters[loneComplex[0]].ParameterType, JsonOptions);

        return values;
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement obj, string name, out JsonElement value)
    {
        foreach (var prop in obj.EnumerateObject())
            if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = prop.Value;
                return true;
            }
        value = default;
        return false;
    }

    /// <summary>
    /// Converts a single argument value to the target type,
    /// using JSON deserialization for complex types when needed.
    /// </summary>
    private static object? ConvertOrDeserialize(object? value, Type targetType)
    {
        if (value is null)
            return targetType.IsValueType ? Activator.CreateInstance(targetType) : null;

        if (targetType.IsInstanceOfType(value))
            return value;

        // JsonElement from SignalR JSON protocol
        if (value is JsonElement element)
            return element.Deserialize(targetType, JsonOptions);

        // String → try JSON deserialization for complex types, direct conversion for simple
        if (value is string str && !IsSimpleType(targetType))
            return JsonSerializer.Deserialize(str, targetType, JsonOptions);

        return ConvertValue(value, targetType);
    }

    private static bool IsSimpleType(Type type)
    {
        var t = Nullable.GetUnderlyingType(type) ?? type;
        return t.IsPrimitive || t == typeof(string) || t == typeof(decimal)
            || t == typeof(DateTime) || t == typeof(DateTimeOffset)
            || t == typeof(Guid) || t.IsEnum;
    }
}

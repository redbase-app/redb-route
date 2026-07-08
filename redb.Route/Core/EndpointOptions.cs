using System.Collections.Concurrent;
using System.Reflection;
using System.Text.RegularExpressions;
using redb.Route.Abstractions;
using redb.Route.Expressions;

namespace redb.Route.Core;

/// <summary>
/// Base class for all endpoint options. Handles URI parameter binding via reflection.
/// Subclass per component with typed properties. BindFromUri maps string params to properties.
/// </summary>
public abstract class EndpointOptions
{
    private Dictionary<string, string>? _unmapped;

    /// <summary>Parameters from URI that were not mapped to typed properties.</summary>
    public IReadOnlyDictionary<string, string> UnmappedParameters =>
        _unmapped ?? (IReadOnlyDictionary<string, string>)new Dictionary<string, string>(0);

    /// <summary>
    /// Binds raw string parameters from URI to typed properties.
    /// - string/int/bool properties: standard conversion.
    /// - DynamicValue{T} properties with ${...}: compiled expression (one time).
    /// - DynamicValue{T} properties without ${...}: static wrap.
    /// - Unmatched parameters go to UnmappedParameters.
    /// </summary>
    /// <param name="parameters">Raw string parameters from EndpointUriParser.</param>
    public void BindFromUri(IReadOnlyDictionary<string, string> parameters)
    {
        if (parameters.Count == 0) return;

        _unmapped = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var props = GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance);

        foreach (var (key, rawValue) in parameters)
        {
            var prop = Array.Find(props, p => p.Name.Equals(key, StringComparison.OrdinalIgnoreCase));
            if (prop == null || !prop.CanWrite)
            {
                _unmapped[key] = rawValue;
                continue;
            }

            var propType = prop.PropertyType;

            if (IsDynamicValueType(propType, out var innerType))
            {
                BindDynamicValue(prop, rawValue, innerType!);
            }
            else
            {
                var converted = ConvertValue(rawValue, propType);
                if (converted != null)
                    prop.SetValue(this, converted);
                else
                    _unmapped[key] = rawValue;
            }
        }
    }

    /// <summary>Validates required parameters and constraints. Called after BindFromUri.</summary>
    public abstract void Validate();

    private ConcurrentDictionary<string, Func<IExchange, string>>? _expressionCache;

    /// <summary>
    /// Resolves a string option value per message.
    /// If the value contains <c>${...}</c> expressions, compiles it once and evaluates per exchange.
    /// Static values pass through with only a fast Contains check.
    /// </summary>
    /// <param name="value">Option value (may contain <c>${...}</c> expressions).</param>
    /// <param name="exchange">Current exchange for expression resolution.</param>
    /// <returns>Resolved value, or the original value if no expressions found.</returns>
    public string? ResolveOption(string? value, IExchange exchange)
    {
        if (value is null || !value.Contains("${")) return value;
        _expressionCache ??= new();
        var resolver = _expressionCache.GetOrAdd(value, static v => ExpressionResolver.GetCompiledTemplate(v));
        return resolver(exchange);
    }

    private static bool IsDynamicValueType(Type type, out Type? innerType)
    {
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(DynamicValue<>))
        {
            innerType = type.GenericTypeArguments[0];
            return true;
        }

        // Nullable<DynamicValue<T>>
        var underlying = Nullable.GetUnderlyingType(type);
        if (underlying != null && underlying.IsGenericType &&
            underlying.GetGenericTypeDefinition() == typeof(DynamicValue<>))
        {
            innerType = underlying.GenericTypeArguments[0];
            return true;
        }

        innerType = null;
        return false;
    }

    private static readonly Regex ExpressionPattern = new(@"\$\{[^}]+\}", RegexOptions.Compiled);

    private void BindDynamicValue(PropertyInfo prop, string rawValue, Type innerType)
    {
        var dvType = typeof(DynamicValue<>).MakeGenericType(innerType);

        // Detect ${...} expression → create dynamic value via StringExpression
        if (ExpressionPattern.IsMatch(rawValue))
        {
            var expression = new StringExpression(rawValue);
            var fromExpr = dvType.GetMethod("FromExpression", [typeof(Abstractions.IExpression)])!;
            var dv = fromExpr.Invoke(null, [expression]);
            prop.SetValue(this, dv);
            return;
        }

        // Plain value → static wrap
        var converted = ConvertValue(rawValue, innerType);
        if (converted == null) return;

        var fromStatic = dvType.GetMethod("FromStatic")!;
        var dv2 = fromStatic.Invoke(null, [converted]);
        prop.SetValue(this, dv2);
    }

    private static object? ConvertValue(string rawValue, Type targetType)
    {
        try
        {
            if (targetType == typeof(string))
                return rawValue;

            if (targetType == typeof(int))
                return int.Parse(rawValue, System.Globalization.CultureInfo.InvariantCulture);

            if (targetType == typeof(long))
                return long.Parse(rawValue, System.Globalization.CultureInfo.InvariantCulture);

            if (targetType == typeof(bool))
                return bool.Parse(rawValue);

            if (targetType == typeof(double))
                return double.Parse(rawValue, System.Globalization.CultureInfo.InvariantCulture);

            if (targetType == typeof(TimeSpan))
                return TimeSpan.Parse(rawValue, System.Globalization.CultureInfo.InvariantCulture);

            if (targetType.IsEnum)
                return Enum.Parse(targetType, rawValue, ignoreCase: true);

            // Nullable<T>
            var underlying = Nullable.GetUnderlyingType(targetType);
            if (underlying != null)
                return ConvertValue(rawValue, underlying);

            return Convert.ChangeType(rawValue, targetType, System.Globalization.CultureInfo.InvariantCulture);
        }
        catch
        {
            return null;
        }
    }
}


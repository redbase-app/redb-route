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

    // Options types whose [Sensitive] properties have already been registered. Reflection runs
    // once per type, not per endpoint.
    private static readonly ConcurrentDictionary<Type, byte> _sensitiveScanned = new();

    /// <summary>
    /// Feeds every <see cref="SensitiveAttribute"/>-marked property name of <paramref name="type"/>
    /// into <see cref="EndpointUri.AddSensitiveKeys"/>, so the URI renderer redacts those parameters
    /// by declaration instead of by name-guessing. Runs once per options type.
    /// <para>
    /// This is the .NET equivalent of the Camel build plugin that generates
    /// <c>SensitiveUtils.SENSITIVE_KEYS</c> from <c>@UriParam(secret = true)</c>: the keyword set is
    /// derived from the declarations, never hand-written. Note the harvest is lazy — it happens when
    /// an options type is first bound — so the name-keyword backstop in
    /// <see cref="EndpointUri.IsSensitiveKey"/> still matters for a URI rendered before any endpoint
    /// of that scheme exists.
    /// </para>
    /// </summary>
    private static void RegisterSensitiveKeys(Type type)
    {
        if (!_sensitiveScanned.TryAdd(type, 0)) return;

        var names = type
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.IsDefined(typeof(SensitiveAttribute), inherit: true))
            .Select(p => p.Name)
            .ToArray();

        if (names.Length > 0)
            EndpointUri.AddSensitiveKeys(names);
    }

    /// <summary>Parameters from URI that were not mapped to typed properties.</summary>
    public IReadOnlyDictionary<string, string> UnmappedParameters =>
        _unmapped ?? (IReadOnlyDictionary<string, string>)new Dictionary<string, string>(0);

    /// <summary>
    /// Binds raw string parameters from URI to typed properties.
    /// - string/int/bool properties: standard conversion.
    /// - DynamicValue{T} properties with ${...}: compiled expression (one time).
    /// - DynamicValue{T} properties without ${...}: static wrap.
    /// - Unmatched parameters go to UnmappedParameters; unless the options type is
    ///   <see cref="LenientPropertiesAttribute">lenient</see>, they are then refused with
    ///   <see cref="ArgumentException"/>, each one named with the nearest option.
    /// - A known option whose value does not convert to its type is refused with
    ///   <see cref="ArgumentException"/>: the option must not silently keep its default.
    /// </summary>
    /// <param name="parameters">Raw string parameters from EndpointUriParser.</param>
    public void BindFromUri(IReadOnlyDictionary<string, string> parameters)
    {
        // Harvest [Sensitive] declarations before anything else, so the set is populated even for
        // an endpoint whose URI carries no parameters at all.
        RegisterSensitiveKeys(GetType());

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
                if (!BindDynamicValue(prop, rawValue, innerType!))
                    throw Unconvertible(prop, key, rawValue, innerType!, acceptsExpressions: true);
            }
            else
            {
                var converted = ConvertValue(rawValue, propType);
                if (converted == null)
                    throw Unconvertible(prop, key, rawValue, propType, acceptsExpressions: false);
                prop.SetValue(this, converted);
            }
        }

        if (WrittenBesideConnectionFactory(GetType(), parameters) is { Count: > 0 } beside)
            throw BesideConnectionFactory(parameters, beside);

        if (_unmapped.Count > 0 && !IsLenient(GetType()))
            throw UnknownParameters(props);
    }

    /// <summary>
    /// Whether <paramref name="optionsType"/> takes parameters it has no property for
    /// (<see cref="LenientPropertiesAttribute"/>). Read by reflection, without an endpoint.
    /// </summary>
    public static bool IsLenient(Type optionsType)
    {
        ArgumentNullException.ThrowIfNull(optionsType);
        return optionsType.IsDefined(typeof(LenientPropertiesAttribute), inherit: true);
    }

    private static readonly ConcurrentDictionary<Type, (PropertyInfo? Reference, HashSet<string> Parameters)> _connectionDeclarations = new();

    /// <summary>
    /// The parameters <paramref name="uri"/> wrote, as written, that a connection factory sets
    /// (<see cref="ConnectionParameterAttribute"/>), when the URI also names a factory
    /// (<see cref="ConnectionFactoryReferenceAttribute"/>). Empty when it names none. The core refuses them when the
    /// options are bound; tooling asks the same question without an endpoint.
    /// </summary>
    /// <exception cref="InvalidOperationException">The type declares connection parameters but no factory reference, or more than one.</exception>
    public static IReadOnlyList<string> WrittenBesideConnectionFactory(Type optionsType, EndpointUri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        return WrittenBesideConnectionFactory(optionsType, uri.RawParameters);
    }

    private static IReadOnlyList<string> WrittenBesideConnectionFactory(Type optionsType, IReadOnlyDictionary<string, string> parameters)
    {
        ArgumentNullException.ThrowIfNull(optionsType);
        var (reference, connectionParameters) = ConnectionDeclarations(optionsType);
        if (reference is null || !parameters.Any(p => p.Key.Equals(reference.Name, StringComparison.OrdinalIgnoreCase)
                                                     && !string.IsNullOrEmpty(p.Value)))
            return [];
        return parameters.Keys.Where(connectionParameters.Contains).ToList();
    }

    private static (PropertyInfo? Reference, HashSet<string> Parameters) ConnectionDeclarations(Type optionsType)
        => _connectionDeclarations.GetOrAdd(optionsType, static type =>
        {
            var props = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);
            var parameters = props.Where(p => p.IsDefined(typeof(ConnectionParameterAttribute), inherit: true))
                .Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var references = props.Where(p => p.IsDefined(typeof(ConnectionFactoryReferenceAttribute), inherit: true)).ToArray();
            if (references.Length > 1)
                throw new InvalidOperationException(
                    $"{type.Name} marks {references.Length} options [ConnectionFactoryReference]; one option names the factory.");
            if (parameters.Count > 0 && references.Length == 0)
                throw new InvalidOperationException(
                    $"{type.Name} declares [ConnectionParameter] options but no [ConnectionFactoryReference] option: the core " +
                    "does not guess which option names the connection factory.");
            return (references.FirstOrDefault(), parameters);
        });

    private ArgumentException BesideConnectionFactory(IReadOnlyDictionary<string, string> parameters, IReadOnlyList<string> beside)
    {
        var reference = ConnectionDeclarations(GetType()).Reference!;
        var factory = parameters.First(p => p.Key.Equals(reference.Name, StringComparison.OrdinalIgnoreCase));
        // By name only: the values are connection settings, credentials among them.
        return new ArgumentException(
            $"{EndpointName(GetType())} endpoint: {factory.Key} '{factory.Value}' sets the whole connection, so " +
            $"{string.Join(", ", beside)} cannot be given on the URI as well. Set them on the factory, or drop " +
            $"{factory.Key} and give the connection on the URI.", beside[0]);
    }

    private static string EndpointName(Type optionsType)
    {
        var name = optionsType.Name;
        foreach (var suffix in new[] { "EndpointOptions", "Options" })
            if (name.EndsWith(suffix, StringComparison.Ordinal) && name.Length > suffix.Length)
                return name[..^suffix.Length];
        return name;
    }

    /// <summary>The side of an endpoint that reads <paramref name="option"/> (<see cref="EndpointRoleAttribute"/>); <c>null</c> for both.</summary>
    public static EndpointRole? RoleOf(PropertyInfo option)
    {
        ArgumentNullException.ThrowIfNull(option);
        return option.GetCustomAttribute<EndpointRoleAttribute>(inherit: true)?.Role;
    }

    /// <summary>
    /// The parameters <paramref name="uri"/> wrote, as written, whose option only the other side of an endpoint reads:
    /// a consumer being created (<paramref name="creating"/> = <see cref="EndpointRole.Consumer"/>) gets the
    /// producer-only ones, and the other way round. The connector refuses them, naming the parameters and never their
    /// values.
    /// </summary>
    /// <param name="optionsType">The endpoint's options type.</param>
    /// <param name="uri">The endpoint URI as written.</param>
    /// <param name="creating">The side being created.</param>
    public static IReadOnlyList<string> WrittenForOtherRole(Type optionsType, EndpointUri uri, EndpointRole creating)
    {
        ArgumentNullException.ThrowIfNull(optionsType);
        ArgumentNullException.ThrowIfNull(uri);
        var otherSide = optionsType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => RoleOf(p) is { } role && role != creating)
            .Select(p => p.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return uri.RawParameters.Keys.Where(otherSide.Contains).ToList();
    }

    /// <summary>
    /// A connector's own words for a parameter name it does not know — typically a name it used to
    /// take and what replaced it — appended to the core refusal, which already names the nearest
    /// option. <c>null</c> = nothing to add.
    /// </summary>
    /// <param name="name">The parameter as the URI wrote it.</param>
    protected virtual string? UnknownParameterHint(string name) => null;

    private ArgumentException UnknownParameters(PropertyInfo[] props)
    {
        var endpoint = EndpointName(GetType());
        var names = props.Where(p => p.CanWrite).Select(p => char.ToLowerInvariant(p.Name[0]) + p.Name[1..]).ToArray();

        // By name only: the value may be a secret.
        var parts = _unmapped!.Keys.Select(key =>
        {
            var text = $"'{key}' is not an option of the {endpoint} endpoint.";
            if (UnknownParameterHint(key) is { Length: > 0 } hint)
                return text + " " + hint;
            return Nearest(key, names) is { } nearest ? $"{text} Did you mean '{nearest}'?" : text;
        });
        return new ArgumentException(string.Join(" ", parts), _unmapped.Keys.First());
    }

    /// <summary>The option name closest to a misspelt one, or <c>null</c> when none is close.</summary>
    private static string? Nearest(string name, string[] candidates)
    {
        string? best = null;
        var bestDistance = int.MaxValue;
        foreach (var candidate in candidates)
        {
            var distance = EditDistance(name.ToLowerInvariant(), candidate.ToLowerInvariant());
            if (distance < bestDistance)
            {
                best = candidate;
                bestDistance = distance;
            }
        }
        // A third of the name, at least two edits: "tll" finds "ttl", "foo" finds nothing.
        return bestDistance <= Math.Max(2, name.Length / 3) ? best : null;
    }

    private static int EditDistance(string a, string b)
    {
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) previous[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1),
                    previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            (previous, current) = (current, previous);
        }
        return previous[b.Length];
    }

    /// <summary>
    /// A connector's own words for an option whose value was refused (what the modes mean, which
    /// one to pick), appended to the core message that already names the value and what the option
    /// takes. <c>null</c> = nothing to add.
    /// </summary>
    /// <param name="optionName">The option's property name.</param>
    protected virtual string? OptionValueHint(string optionName) => null;

    private ArgumentException Unconvertible(PropertyInfo prop, string key, string rawValue, Type type, bool acceptsExpressions)
    {
        var shown = prop.IsDefined(typeof(SensitiveAttribute), inherit: true) ? "***" : rawValue;
        var message = $"'{key}={shown}' is not a valid value: {key} takes {Describe(key, type)}.";
        if (!acceptsExpressions && rawValue.Contains("${", StringComparison.Ordinal))
            message += " This option does not accept ${...} expressions; use a constant or a {{property}} placeholder.";
        if (OptionValueHint(prop.Name) is { Length: > 0 } hint)
            message += " " + hint;
        return new ArgumentException(message, key);
    }

    private static string Describe(string key, Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (type.IsEnum)
            // Written the way the URI takes them, so the message can be copied back as is.
            return "one of " + string.Join(", ", Enum.GetNames(type).Select(n => $"{key}={n.ToLowerInvariant()}"));
        if (type == typeof(bool))
            return "true or false";
        if (type == typeof(int) || type == typeof(long) || type == typeof(short) || type == typeof(byte)
            || type == typeof(uint) || type == typeof(ulong) || type == typeof(ushort) || type == typeof(sbyte))
            return $"a whole number ({type.Name})";
        if (type == typeof(double) || type == typeof(float) || type == typeof(decimal))
            return "a number with a '.' decimal point";
        if (type == typeof(TimeSpan))
            return "a time span (hh:mm:ss)";
        return type.Name;
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

    /// <summary>Binds a dynamic option; <c>false</c> when a plain value does not convert.</summary>
    private bool BindDynamicValue(PropertyInfo prop, string rawValue, Type innerType)
    {
        var dvType = typeof(DynamicValue<>).MakeGenericType(innerType);

        // Detect ${...} expression → create dynamic value via StringExpression
        if (ExpressionPattern.IsMatch(rawValue))
        {
            var expression = new StringExpression(rawValue);
            var fromExpr = dvType.GetMethod("FromExpression", [typeof(Abstractions.IExpression)])!;
            var dv = fromExpr.Invoke(null, [expression]);
            prop.SetValue(this, dv);
            return true;
        }

        // Plain value → static wrap
        var converted = ConvertValue(rawValue, innerType);
        if (converted == null) return false;

        var fromStatic = dvType.GetMethod("FromStatic")!;
        var dv2 = fromStatic.Invoke(null, [converted]);
        prop.SetValue(this, dv2);
        return true;
    }

    private static object? ConvertValue(string rawValue, Type targetType)
    {
        var converted = OptionValueConverter.Convert(rawValue, targetType);
        // Enum.Parse also takes a number, which may name no member at all. A [Flags] enum is left
        // alone: its valid values are combinations, not members.
        var enumType = Nullable.GetUnderlyingType(targetType) ?? targetType;
        if (converted is not null && enumType.IsEnum && !enumType.IsDefined(typeof(FlagsAttribute), inherit: false)
            && !Enum.IsDefined(enumType, converted))
            return null;
        return converted;
    }

    /// <summary>
    /// Registers the <see cref="SensitiveAttribute"/>-marked property names of an arbitrary type
    /// with the URI redaction set — the same harvest <see cref="BindFromUri"/> performs for options
    /// types, reusable by components that bind URI parameters onto user objects (<c>bean:</c>).
    /// </summary>
    internal static void RegisterSensitiveKeysFor(Type type) => RegisterSensitiveKeys(type);
}

/// <summary>
/// The one string-to-typed-value converter behind <see cref="EndpointOptions.BindFromUri"/>,
/// extracted for reuse (Route-XML Ф1: <c>bean:</c> property binding, the XML loader's value
/// conversion). Invariant culture throughout; an unconvertible value yields null and the caller
/// decides whether that is an error or an unmapped parameter.
/// </summary>
internal static class OptionValueConverter
{
    /// <summary>Converts a raw URI/attribute string to <paramref name="targetType"/>, or null.</summary>
    internal static object? Convert(string rawValue, Type targetType)
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

            // Float, not the default Float | AllowThousands: "1,5" is a comma typed for a decimal
            // point, and reading it as 15 would be a silent wrong value.
            if (targetType == typeof(double))
                return double.Parse(rawValue, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture);

            if (targetType == typeof(float))
                return float.Parse(rawValue, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture);

            if (targetType == typeof(decimal))
                return decimal.Parse(rawValue, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture);

            if (targetType == typeof(TimeSpan))
                return TimeSpan.Parse(rawValue, System.Globalization.CultureInfo.InvariantCulture);

            if (targetType.IsEnum)
                return Enum.Parse(targetType, rawValue, ignoreCase: true);

            // Nullable<T>
            var underlying = Nullable.GetUnderlyingType(targetType);
            if (underlying != null)
                return Convert(rawValue, underlying);

            return System.Convert.ChangeType(rawValue, targetType, System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException or ArgumentException)
        {
            // Not convertible to the requested type: the caller treats null as "unmapped" (options)
            // or as a declaration error (bean:). Anything else — reflection failures, type-load
            // errors — keeps flying.
            return null;
        }
    }
}


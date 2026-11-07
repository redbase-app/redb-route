using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Path;
using redb.Route.Abstractions;

namespace redb.Route.Expressions;

/// <summary>
/// Expression for evaluating JSONPath (RFC 9535, JsonPath.Net) queries against the message body.
/// </summary>
/// <remarks>
/// Accepts JSON text, <c>byte[]</c>, <see cref="Stream"/>, a System.Text.Json tree (<see cref="JsonNode"/>,
/// <see cref="JsonElement"/>, <see cref="JsonDocument"/>) or a POCO (serialized with its C# member names).
/// JSON text may contain comments and trailing commas. Results convert to the requested type the way
/// they always did: a scalar to <typeparamref name="T"/>, an array to a typed CLR array / list, an object
/// to a POCO or dictionary, anything to <c>string</c> as its text (compact JSON for arrays and objects,
/// invariant culture for numbers). Dates stay strings unless a <see cref="DateTime"/> is asked for.
/// </remarks>
public class JsonPathExpression : Expression
{
    private static readonly JsonDocumentOptions LenientText = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
    private static readonly JsonSerializerOptions PocoOptions = new();   // C# member names, as JToken.FromObject kept them
    private static readonly PathParsingOptions ParsingOptions = new() { AllowMathOperations = true, AllowInOperator = true, AllowJsonConstructs = true, TolerateExtraWhitespace = true };

    private readonly string _jsonPath;
    private readonly JsonPath _compiled;
    private readonly bool _isFilter;
    private readonly IExpression? _source;

    /// <summary>
    /// Initializes a new instance of the <see cref="JsonPathExpression"/> class.
    /// </summary>
    /// <param name="jsonPath">The JSONPath expression used to extract data.</param>
    /// <param name="source">
    /// What to run the path against. <c>null</c> — the default — means the message body.
    /// </param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="jsonPath"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentException">Thrown when the path does not parse (so a route fails at build, not on the first message).</exception>
    public JsonPathExpression(string jsonPath, IExpression? source = null)
    {
        _source = source;
        _jsonPath = jsonPath ?? throw new ArgumentNullException(nameof(jsonPath));
        try
        {
            _compiled = JsonPath.Parse(jsonPath, ParsingOptions);
        }
        catch (PathParseException ex)
        {
            throw new ArgumentException($"Invalid JSONPath '{jsonPath}': {ex.Message}", nameof(jsonPath), ex);
        }
        _isFilter = jsonPath.Contains("[?", StringComparison.Ordinal);
    }

    /// <summary>The JSONPath text.</summary>
    public string Path => _jsonPath;

    /// <summary>
    /// Returns the same expression reading <paramref name="source"/> instead of the message body.
    /// </summary>
    /// <remarks>
    /// <c>JPath("$.id").From(Property("original-payload"))</c>. Without it, querying JSON that
    /// arrived in a header or property means moving it into the body first, which damages the body
    /// for the rest of the route.
    /// </remarks>
    /// <param name="source">The expression producing the JSON to read.</param>
    /// <returns>A new expression; this one is unchanged.</returns>
    public JsonPathExpression From(IExpression source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return new JsonPathExpression(_jsonPath, source);
    }

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">Thrown when the body is not JSON or the value cannot be converted.</exception>
    public override T Evaluate<T>(IExchange exchange)
        => EvaluateOn<T>(ExpressionValues.ReadInput(_source, exchange), _source is not null);

    /// <summary>
    /// Runs the path against an already-resolved input. Used by the expression language, where the
    /// source arrives as a value rather than as an expression.
    /// </summary>
    internal T EvaluateOn<T>(object? input, bool fromSource)
    {
        var body = input;
        if (body is null)
            throw new InvalidOperationException(ExpressionValues.NoInput(fromSource, "JsonPath"));

        JsonNode? root;
        try
        {
            root = JsonPathValues.ToNode(body, LenientText, PocoOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"JSON parsing error in JsonPathExpression: {ex.Message}", ex);
        }

        var matches = _compiled.Evaluate(root).Matches;
        var nodes = matches is null ? [] : matches.Select(m => m.Value).ToList();

        try
        {
            return _compiled.IsSingular
                ? JsonPathValues.ConvertSingle<T>(nodes.Count == 0 ? null : nodes[0], _jsonPath)
                : JsonPathValues.ConvertMany<T>(nodes, _jsonPath, _isFilter);
        }
        catch (InvalidOperationException) { throw; }
        catch (Exception ex) when (ex is InvalidCastException or FormatException or JsonException or OverflowException)
        {
            throw new InvalidOperationException($"Error evaluating JsonPath '{_jsonPath}': {ex.Message}", ex);
        }
    }

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">Always thrown because <see cref="JsonPathExpression"/> does not support setting values.</exception>
    public override void SetValue(IExchange exchange, object value)
    {
        throw new NotSupportedException("JsonPathExpression does not support setting values.");
    }

    /// <inheritdoc />
    public override string ToTemplateString() => $"${{jsonpath({_jsonPath})}}";
}

/// <summary>Body → tree and node → CLR conversions of <see cref="JsonPathExpression"/>, kept apart so the rules read in one place.</summary>
internal static class JsonPathValues
{
    public static JsonNode? ToNode(object body, JsonDocumentOptions lenient, JsonSerializerOptions pocoOptions) => body switch
    {
        JsonNode node => node,
        JsonElement element => JsonSerializer.SerializeToNode(element),
        JsonDocument document => JsonSerializer.SerializeToNode(document.RootElement),
        string text => JsonNode.Parse(text, documentOptions: lenient),
        byte[] bytes => JsonNode.Parse(bytes, documentOptions: lenient),
        Stream stream => ParseStream(stream, lenient),
        _ => JsonSerializer.SerializeToNode(body, pocoOptions),
    };

    /// <summary>
    /// A stream body is read from its current position and left there afterwards, so a second
    /// expression on the same exchange (or a redelivery) sees the same JSON. A stream that cannot seek
    /// is consumed — there is nothing to restore.
    /// </summary>
    private static JsonNode? ParseStream(Stream stream, JsonDocumentOptions lenient)
    {
        if (!stream.CanSeek)
            return JsonNode.Parse(stream, documentOptions: lenient);

        var position = stream.Position;
        try
        {
            return JsonNode.Parse(stream, documentOptions: lenient);
        }
        finally
        {
            stream.Position = position;
        }
    }

    /// <summary>A singular path: one node or nothing (the old <c>SelectToken</c> branch).</summary>
    public static T ConvertSingle<T>(JsonNode? node, string path)
    {
        if (node is null)
        {
            if (IsNonNullableValueType<T>())
                throw new InvalidOperationException($"No value found for JsonPath: {path}");
            return default!;
        }
        return (T)ConvertNode(node, typeof(T), path)!;
    }

    /// <summary>A non-singular path (wildcard, descent, slice, filter): the old <c>SelectTokens</c> branch.</summary>
    public static T ConvertMany<T>(List<JsonNode?> nodes, string path, bool isFilter)
    {
        if (isFilter && typeof(T) == typeof(bool))
            return (T)(object)(nodes.Count > 0);

        // A single scalar match unwraps — unless the caller asked for a collection, which keeps the array shape.
        if (nodes.Count == 1 && nodes[0] is JsonValue single && !IsCollectionType(typeof(T)))
            return (T)ConvertNode(single, typeof(T), path)!;

        if (nodes.Count > 0)
        {
            var array = new JsonArray(nodes.Select(n => n?.DeepClone()).ToArray());
            if (typeof(T) == typeof(string))
                return (T)(object)(nodes.All(n => n is null or JsonValue)
                    ? string.Join(", ", nodes.Select(n => Text(n)))
                    : array.ToJsonString());
            return (T)ConvertNode(array, typeof(T), path)!;
        }

        if (IsNonNullableValueType<T>())
            throw new InvalidOperationException($"No values found for JsonPath: {path}");
        return default!;
    }

    private static object? ConvertNode(JsonNode? node, Type target, string path)
    {
        if (node is null)
        {
            if (target.IsValueType && Nullable.GetUnderlyingType(target) is null)
                throw new InvalidOperationException($"JsonPath returned null but type {target.Name} is not nullable");
            return null;
        }

        if (target == typeof(JsonNode) || target == node.GetType()) return node;

        switch (node)
        {
            case JsonArray array:
                if (target == typeof(string)) return array.ToJsonString();
                if (target == typeof(object) || target == typeof(object[])) return TypedArray(array, target == typeof(object[]));
                return array.Deserialize(target)
                    ?? throw new InvalidOperationException($"Cannot convert JsonPath '{path}' array to {target.Name}");

            case JsonObject obj:
                if (target == typeof(object)) return obj;
                if (target == typeof(string)) return obj.ToJsonString();
                return obj.Deserialize(target)
                    ?? throw new InvalidOperationException($"Cannot convert JsonPath '{path}' object to {target.Name}");

            case JsonValue value:
                var primitive = Primitive(value);
                if (primitive is null)
                {
                    if (target.IsValueType && Nullable.GetUnderlyingType(target) is null)
                        throw new InvalidOperationException($"JsonPath returned null but type {target.Name} is not nullable");
                    return null;
                }
                var effective = Nullable.GetUnderlyingType(target) ?? target;
                if (effective == typeof(object) || effective.IsInstanceOfType(primitive)) return primitive;
                if (effective == typeof(string)) return Text(value);
                if (effective == typeof(DateTimeOffset)) return DateTimeOffset.Parse(Text(value), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
                if (effective == typeof(DateTime)) return DateTime.Parse(Text(value), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
                if (effective == typeof(Guid)) return Guid.Parse(Text(value));
                if (effective.IsEnum) return Enum.Parse(effective, Text(value), ignoreCase: true);
                return Convert.ChangeType(primitive, effective, CultureInfo.InvariantCulture);
        }

        return node.Deserialize(target);
    }

    /// <summary>Homogeneous scalar arrays become typed CLR arrays (<c>int[]</c>, <c>string[]</c>, <c>double[]</c>, <c>bool[]</c>); anything else <c>object[]</c>.</summary>
    private static object TypedArray(JsonArray array, bool forceObject)
    {
        var items = array.Select(n => n is JsonValue v ? Primitive(v) : n).ToArray();
        if (forceObject || items.Length == 0 || items.Any(i => i is null or JsonNode))
            return items;

        var elementType = items[0]!.GetType();
        if (items.All(i => i!.GetType() == elementType))
        {
            var kind = elementType == typeof(long) && items.All(i => (long)i! is >= int.MinValue and <= int.MaxValue) ? typeof(int) : elementType;
            var typed = Array.CreateInstance(kind, items.Length);
            for (var i = 0; i < items.Length; i++)
                typed.SetValue(kind == typeof(int) ? (object)(int)(long)items[i]! : items[i], i);
            return typed;
        }
        return items;
    }

    /// <summary>
    /// JSON scalar → CLR: strings stay strings, integers are <c>long</c>, other numbers <c>double</c>,
    /// booleans <c>bool</c>. A <see cref="JsonValue"/> may be backed by a parsed element or by a CLR value
    /// the path engine produced (a double even for a whole number), so both backings are read.
    /// </summary>
    private static object? Primitive(JsonValue value)
    {
        if (value.TryGetValue<string>(out var s)) return s;
        if (value.TryGetValue<bool>(out var b)) return b;
        if (value.TryGetValue<long>(out var l)) return l;
        if (value.TryGetValue<int>(out var i)) return (long)i;
        if (value.TryGetValue<double>(out var d))
            return d == Math.Floor(d) && Math.Abs(d) < 9.2e18 && !double.IsInfinity(d) ? (long)d : d;
        if (value.TryGetValue<decimal>(out var m))
            return m == decimal.Truncate(m) && Math.Abs(m) < 9.2e18m ? (long)m : (double)m;

        var element = value.GetValue<JsonElement>();
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            JsonValueKind.Number => element.TryGetInt64(out var el) ? el : element.GetDouble(),
            _ => element.GetRawText(),
        };
    }

    private static string Text(JsonNode? node) => node switch
    {
        null => string.Empty,
        JsonValue value => Primitive(value) switch
        {
            null => string.Empty,
            string s => s,
            bool b => b ? "true" : "false",
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            var other => other.ToString() ?? string.Empty,
        },
        _ => node.ToJsonString(),
    };

    private static bool IsNonNullableValueType<T>() => typeof(T).IsValueType && Nullable.GetUnderlyingType(typeof(T)) is null;

    private static bool IsCollectionType(Type type)
        => type != typeof(string) && (type.IsArray || typeof(System.Collections.IEnumerable).IsAssignableFrom(type));
}

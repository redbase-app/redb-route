using System.Globalization;
using redb.Route.Abstractions;
using redb.Route.Expressions;

namespace redb.Route.Aggregation;

/// <summary>
/// Ready-made aggregation strategies (Apache Camel <c>AggregationStrategies</c> parity). Each is the
/// existing <c>Func&lt;IExchange, IExchange, IExchange&gt;</c> the DSL already takes, so one strategy
/// serves <c>Aggregate</c>, <c>Multicast</c>, <c>ScatterGather</c>, <c>Split</c> and <c>Enrich</c>.
/// <para>
/// Argument order is <c>(accumulated, incoming)</c>; for <c>Enrich</c> that is <c>(original, resource)</c>.
/// Every strategy tolerates a <c>null</c> accumulator (Split calls the strategy for the first fragment
/// with <c>null</c>, Camel-style) and an accumulator that is still a plain first exchange (Multicast and
/// Aggregate start with the first result as the accumulator).
/// </para>
/// Named forms for the XML format: <see cref="ByName"/>.
/// </summary>
public static class AggregationStrategies
{
    /// <summary>
    /// The list <see cref="GroupedBody()"/> builds. A private subtype, so the strategy appends only to a
    /// list it created itself: bodies are cloned by reference, and an incoming <c>List&lt;object?&gt;</c>
    /// body is shared by the caller and every branch clone — appending to it would mutate the caller's
    /// data and, when a branch kept that list as its body, add the list to itself.
    /// </summary>
    private sealed class GroupedList : List<object?>;

    /// <summary>Typed counterpart of <see cref="GroupedList"/> for <see cref="GroupedBody{T}"/>.</summary>
    private sealed class GroupedList<T> : List<T>;

    /// <summary>Collects the bodies into a <c>List&lt;object?&gt;</c> in the accumulated body.</summary>
    public static Func<IExchange, IExchange, IExchange> GroupedBody() => (old, @new) =>
    {
        if (old is null) { @new.In.Body = new GroupedList { @new.In.Body }; return @new; }
        if (old.In.Body is GroupedList list) list.Add(@new.In.Body);
        else old.In.Body = new GroupedList { old.In.Body, @new.In.Body };
        return old;
    };

    /// <summary>Collects the bodies into a typed <c>List&lt;T&gt;</c>; a body that is not a <typeparamref name="T"/> fails with an explicit message.</summary>
    public static Func<IExchange, IExchange, IExchange> GroupedBody<T>() => (old, @new) =>
    {
        if (old is null) { @new.In.Body = new GroupedList<T> { As<T>(@new.In.Body) }; return @new; }
        if (old.In.Body is GroupedList<T> list) list.Add(As<T>(@new.In.Body));
        else old.In.Body = new GroupedList<T> { As<T>(old.In.Body), As<T>(@new.In.Body) };
        return old;
    };

    /// <summary>Collects the exchanges themselves into a <c>List&lt;IExchange&gt;</c> (for a Split whose fragments are processed later).</summary>
    public static Func<IExchange, IExchange, IExchange> GroupedExchange() => (old, @new) =>
    {
        if (old is null) { @new.In.Body = new List<IExchange> { @new }; return @new; }
        if (old.In.Body is List<IExchange> list) list.Add(@new);
        else old.In.Body = new List<IExchange> { old, @new };
        return old;
    };

    /// <summary>The incoming exchange wins (for <c>Enrich</c>: the response becomes the current message). Camel's default when no strategy is given.</summary>
    public static Func<IExchange, IExchange, IExchange> UseLatest() => (_, @new) => @new;

    /// <summary>The accumulated (first / original) exchange wins; the incoming one is dropped.</summary>
    public static Func<IExchange, IExchange, IExchange> UseOriginal() => (old, @new) => old ?? @new;

    /// <summary>Bodies as strings joined with <paramref name="separator"/>.</summary>
    public static Func<IExchange, IExchange, IExchange> Concat(string separator = "")
    {
        ArgumentNullException.ThrowIfNull(separator);
        return (old, @new) =>
        {
            if (old is null) { @new.In.Body = Text(@new.In.Body); return @new; }
            old.In.Body = Text(old.In.Body) + separator + Text(@new.In.Body);
            return old;
        };
    }

    /// <summary>Keeps the accumulated body; headers of the incoming exchange are merged in (the later one wins).</summary>
    public static Func<IExchange, IExchange, IExchange> MergeHeaders() => (old, @new) =>
    {
        if (old is null) return @new;
        foreach (var (key, value) in @new.In.Headers) old.In.Headers[key] = value;
        return old;
    };

    /// <summary>Puts the incoming body into header <paramref name="name"/> of the accumulated exchange — the most common <c>Enrich</c>.</summary>
    public static Func<IExchange, IExchange, IExchange> IntoHeader(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return (old, @new) =>
        {
            if (old is null) return @new;
            old.In.Headers[name] = @new.In.Body;
            return old;
        };
    }

    /// <summary>Puts the incoming body into exchange property <paramref name="key"/> of the accumulated exchange.</summary>
    public static Func<IExchange, IExchange, IExchange> IntoProperty(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return (old, @new) =>
        {
            if (old is null) return @new;
            old.Properties[key] = @new.In.Body;
            return old;
        };
    }

    /// <summary>Numeric sum of a route-language expression (<c>"body.amount"</c>) over the exchanges; the running total is the accumulated body (decimal).</summary>
    public static Func<IExchange, IExchange, IExchange> Sum(string expression) => Fold(new StringExpression(expression), (a, b) => a + b);

    /// <summary>Numeric sum of <paramref name="expression"/> over the exchanges.</summary>
    public static Func<IExchange, IExchange, IExchange> Sum(IExpression expression) => Fold(expression, (a, b) => a + b);

    /// <summary>Maximum of a route-language expression over the exchanges.</summary>
    public static Func<IExchange, IExchange, IExchange> Max(string expression) => Fold(new StringExpression(expression), Math.Max);

    /// <summary>Maximum of <paramref name="expression"/> over the exchanges.</summary>
    public static Func<IExchange, IExchange, IExchange> Max(IExpression expression) => Fold(expression, Math.Max);

    /// <summary>Minimum of a route-language expression over the exchanges.</summary>
    public static Func<IExchange, IExchange, IExchange> Min(string expression) => Fold(new StringExpression(expression), Math.Min);

    /// <summary>Minimum of <paramref name="expression"/> over the exchanges.</summary>
    public static Func<IExchange, IExchange, IExchange> Min(IExpression expression) => Fold(expression, Math.Min);

    /// <summary>A caller-supplied strategy, wrapped so it reads like the others in a chain.</summary>
    public static Func<IExchange, IExchange, IExchange> Custom(Func<IExchange, IExchange, IExchange> strategy)
        => strategy ?? throw new ArgumentNullException(nameof(strategy));

    /// <summary>
    /// Resolves a strategy from its XML / configuration name: <c>groupedBody</c>, <c>groupedExchange</c>,
    /// <c>useLatest</c>, <c>useOriginal</c>, <c>mergeHeaders</c>, <c>concat:&lt;separator&gt;</c>,
    /// <c>intoHeader:&lt;name&gt;</c>, <c>intoProperty:&lt;key&gt;</c>, <c>sum:&lt;expression&gt;</c>,
    /// <c>max:&lt;expression&gt;</c>, <c>min:&lt;expression&gt;</c>. Case-insensitive.
    /// </summary>
    public static Func<IExchange, IExchange, IExchange> ByName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var colon = name.IndexOf(':');
        var head = (colon >= 0 ? name[..colon] : name).Trim();
        var argument = colon >= 0 ? name[(colon + 1)..] : null;

        return head.ToLowerInvariant() switch
        {
            "groupedbody" => GroupedBody(),
            "groupedexchange" => GroupedExchange(),
            "uselatest" => UseLatest(),
            "useoriginal" => UseOriginal(),
            "mergeheaders" => MergeHeaders(),
            "concat" => Concat(argument ?? ""),
            "intoheader" => IntoHeader(Required(argument, name)),
            "intoproperty" => IntoProperty(Required(argument, name)),
            "sum" => Sum(Required(argument, name)),
            "max" => Max(Required(argument, name)),
            "min" => Min(Required(argument, name)),
            _ => throw new ArgumentException(
                $"Unknown aggregation strategy '{name}'. Known: groupedBody, groupedExchange, useLatest, useOriginal, mergeHeaders, " +
                "concat:<separator>, intoHeader:<name>, intoProperty:<key>, sum:<expression>, max:<expression>, min:<expression>.", nameof(name)),
        };
    }

    private static string Required(string? argument, string name)
        => string.IsNullOrWhiteSpace(argument)
            ? throw new ArgumentException($"Aggregation strategy '{name}' needs an argument after the colon.", nameof(name))
            : argument;

    /// <summary>Marks an accumulator whose body is the running value of a numeric fold.</summary>
    private const string FoldMarker = "__redb_agg:fold";

    /// <summary>
    /// Numeric fold. The accumulated body holds the running value once aggregation has started — marked
    /// as such on the accumulator, because "the body is a number" is not proof: a plain first exchange
    /// (Multicast / Aggregate seed the accumulator with the first result) may carry a numeric body of its
    /// own, and its value must still be taken through the expression.
    /// </summary>
    private static Func<IExchange, IExchange, IExchange> Fold(IExpression expression, Func<decimal, decimal, decimal> combine)
    {
        ArgumentNullException.ThrowIfNull(expression);
        return (old, @new) =>
        {
            var incoming = Number(expression.Evaluate<object>(@new), expression);
            if (old is null)
            {
                @new.In.Body = incoming;
                @new.Properties[FoldMarker] = true;
                return @new;
            }

            var running = old.Properties.ContainsKey(FoldMarker) && old.In.Body is { } body && IsNumber(body)
                ? Convert.ToDecimal(body, CultureInfo.InvariantCulture)
                : Number(expression.Evaluate<object>(old), expression);
            old.In.Body = combine(running, incoming);
            old.Properties[FoldMarker] = true;
            return old;
        };
    }

    private static decimal Number(object? value, IExpression expression) => value switch
    {
        null => throw new InvalidOperationException($"Aggregation: expression '{expression}' evaluated to null; a number is required."),
        _ when IsNumber(value) => Convert.ToDecimal(value, CultureInfo.InvariantCulture),
        string s when decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed) => parsed,
        _ => throw new InvalidOperationException($"Aggregation: expression '{expression}' evaluated to '{value}' ({value.GetType().Name}); a number is required."),
    };

    private static bool IsNumber(object value) => value is sbyte or byte or short or ushort or int or uint or long or ulong or float or double or decimal;

    private static string Text(object? body) => body switch
    {
        null => string.Empty,
        string s => s,
        byte[] bytes => System.Text.Encoding.UTF8.GetString(bytes),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => body.ToString() ?? string.Empty,
    };

    private static T As<T>(object? body) => body switch
    {
        T typed => typed,
        null when default(T) is null => default!,
        _ => throw new InvalidOperationException($"GroupedBody<{typeof(T).Name}>: body is {body?.GetType().Name ?? "null"}, not {typeof(T).Name}."),
    };
}

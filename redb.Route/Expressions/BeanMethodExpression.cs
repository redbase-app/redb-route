using redb.Route.Abstractions;
using redb.Route.Components.Bean;
using redb.Route.Predicates;

namespace redb.Route.Expressions;

/// <summary>
/// What <see cref="Core.RouteBuilder.Method"/> builds: a registry bean's method as an expression and a condition,
/// Apache Camel's <c>method(ref, "name")</c>. The same call as <c>${bean:name.Method(args)}</c>, typed and written in
/// the <c>bean:</c> URI spelling (<c>IsGold(${header.name})</c>), through the same binding and the same per-context
/// resolution. One difference: in a condition the engine awaits <see cref="IPredicate.MatchesAsync"/>, so a method
/// returning <c>Task&lt;bool&gt;</c> works there; a value position (<see cref="Evaluate{T}"/>) is synchronous and refuses
/// it as the function does.
/// <para>
/// Exposed as <see cref="IPredicateExpression"/>, so <c>Filter(Method(...))</c> picks the expression overload
/// without ambiguity; the condition path finds the <see cref="IPredicate"/> it also is.
/// </para>
/// </summary>
internal sealed class BeanMethodExpression : Expression, IPredicateExpression, IPredicate
{
    private readonly string _reference;
    private readonly string _spec;
    private readonly StringExpression[] _arguments;
    private readonly BeanCallSite _site;

    internal BeanMethodExpression(string reference, string methodSpec)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        ArgumentException.ThrowIfNullOrWhiteSpace(methodSpec);
        var name = reference.Trim();
        if (name.Contains(',', StringComparison.Ordinal) || (!name.StartsWith('#') && name.Contains('.', StringComparison.Ordinal)))
            throw new ArgumentException(
                $"Method(\"{reference}\", ...): in an expression the bean is a registry name (\"#name\"). " +
                "Declare an instance of a type with <bean name type> or AddToRegistry first.", nameof(reference));
        _reference = name.TrimStart('#');
        _spec = methodSpec.Trim();
        var (method, arguments) = BeanInvoker.ParseMethodSpec(_spec, "#" + _reference);
        _arguments = [.. arguments.Select(a => new StringExpression(a))];
        _site = new BeanCallSite(_reference, method, _arguments.Length);
    }

    private object?[] Values(IExchange exchange) => [.. _arguments.Select(a => a.Evaluate<object>(exchange))];

    /// <inheritdoc />
    public override T Evaluate<T>(IExchange exchange)
    {
        ArgumentNullException.ThrowIfNull(exchange);
        var result = _site.Invoke(exchange, Values(exchange));
        if (result is null)
            return default!;
        if (result is T typed)
            return typed;
        if (typeof(T) == typeof(string))
            return (T)(object)ExpressionResolver.TemplateText(result);
        return (T)Convert.ChangeType(result, typeof(T), System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <inheritdoc cref="IPredicateExpression.Matches" />
    public bool Matches(IExchange exchange) => RouteTruthiness.ToBoolean(Evaluate<object>(exchange));

    /// <inheritdoc />
    public async Task<bool> MatchesAsync(IExchange exchange)
    {
        ArgumentNullException.ThrowIfNull(exchange);
        return RouteTruthiness.ToBoolean(await _site.InvokeAsync(exchange, Values(exchange), CancellationToken.None).ConfigureAwait(false));
    }

    /// <inheritdoc />
    public override void SetValue(IExchange exchange, object value)
        => throw new NotSupportedException($"Method(\"#{_reference}\", \"{_spec}\") is a call; it cannot be assigned.");

    /// <inheritdoc />
    public override string ToTemplateString() => $"${{bean:{_reference}.{_spec}}}";

    /// <inheritdoc />
    public override string ToString() => $"Method(\"#{_reference}\", \"{_spec}\")";
}

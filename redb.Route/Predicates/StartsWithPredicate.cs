using redb.Route.Abstractions;

namespace redb.Route.Predicates;

/// <summary>
/// Predicate that checks whether a string expression result starts with a given value.
/// </summary>
public class StartsWithPredicate : IPredicate
{
    private readonly IExpression _expression;
    private readonly string _value;

    /// <summary>
    /// Initializes a new instance with the specified expression and prefix value.
    /// </summary>
    /// <param name="expression">The expression to evaluate.</param>
    /// <param name="value">The prefix to check for.</param>
    public StartsWithPredicate(IExpression expression, object value)
    {
        _expression = expression;
        _value = value?.ToString() ?? string.Empty;
    }

    /// <inheritdoc />
    public bool Matches(IExchange exchange)
    {
        var result = _expression.Evaluate<string>(exchange);
        return result?.StartsWith(_value) == true;
    }

    /// <inheritdoc />
    public Task<bool> MatchesAsync(IExchange exchange)
        => Task.FromResult(Matches(exchange));
}

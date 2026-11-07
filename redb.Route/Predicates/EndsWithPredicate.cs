using redb.Route.Abstractions;

namespace redb.Route.Predicates;

/// <summary>
/// Predicate that checks whether a string expression result ends with a given value.
/// </summary>
public class EndsWithPredicate : IPredicate
{
    private readonly IExpression _expression;
    private readonly string _value;

    /// <summary>
    /// Initializes a new instance with the specified expression and suffix value.
    /// </summary>
    /// <param name="expression">The expression to evaluate.</param>
    /// <param name="value">The suffix to check for.</param>
    public EndsWithPredicate(IExpression expression, object value)
    {
        _expression = expression;
        _value = value?.ToString() ?? string.Empty;
    }

    /// <inheritdoc />
    public bool Matches(IExchange exchange)
    {
        var result = _expression.Evaluate<string>(exchange);
        return result?.EndsWith(_value) == true;
    }

    /// <inheritdoc />
    public Task<bool> MatchesAsync(IExchange exchange)
        => Task.FromResult(Matches(exchange));
}

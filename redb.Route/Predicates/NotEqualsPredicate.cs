using redb.Route.Abstractions;

namespace redb.Route.Predicates;

/// <summary>
/// Predicate that checks whether an expression result does not equal a given value.
/// </summary>
public class NotEqualsPredicate : IPredicate
{
    private readonly IExpression _expression;
    private readonly object _value;

    /// <summary>
    /// Initializes a new instance with the specified expression and comparison value.
    /// </summary>
    /// <param name="expression">The expression to evaluate.</param>
    /// <param name="value">The value to compare against.</param>
    public NotEqualsPredicate(IExpression expression, object value)
    {
        _expression = expression;
        _value = value;
    }

    /// <inheritdoc />
    public bool Matches(IExchange exchange)
    {
        var result = _expression.Evaluate<object>(exchange);
        return !object.Equals(result, _value);
    }

    /// <inheritdoc />
    public Task<bool> MatchesAsync(IExchange exchange)
        => Task.FromResult(Matches(exchange));
}

using redb.Route.Abstractions;

namespace redb.Route.Predicates;

/// <summary>
/// Predicate that checks whether an expression result equals a given value.
/// </summary>
public class EqualsPredicate : IPredicate
{
    private readonly IExpression _expression;
    private readonly object _value;

    /// <summary>
    /// Initializes a new instance with the specified expression and comparison value.
    /// </summary>
    /// <param name="expression">The expression to evaluate.</param>
    /// <param name="value">The value to compare against.</param>
    public EqualsPredicate(IExpression expression, object value)
    {
        _expression = expression;
        _value = value;
    }

    /// <inheritdoc />
    public bool Matches(IExchange exchange)
    {
        var result = _expression.Evaluate<object>(exchange);
        return Equals(result, _value);
    }

    /// <inheritdoc />
    public Task<bool> MatchesAsync(IExchange exchange)
        => Task.FromResult(Matches(exchange));
}

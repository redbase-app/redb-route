using redb.Route.Abstractions;

namespace redb.Route.Predicates;

/// <summary>
/// Predicate that checks whether an expression result is less than a given value.
/// </summary>
public class LessThanPredicate : IPredicate
{
    private readonly IExpression _expression;
    private readonly IComparable _value;

    /// <summary>
    /// Initializes a new instance with the specified expression and comparison value.
    /// </summary>
    /// <param name="expression">The expression to evaluate.</param>
    /// <param name="value">The value to compare against (must implement <see cref="IComparable"/>).</param>
    public LessThanPredicate(IExpression expression, object value)
    {
        _expression = expression;
        _value = (IComparable)value;
    }

    /// <inheritdoc />
    public bool Matches(IExchange exchange)
    {
        var result = _expression.Evaluate<IComparable>(exchange);
        return result?.CompareTo(_value) < 0;
    }

    /// <inheritdoc />
    public Task<bool> MatchesAsync(IExchange exchange)
        => Task.FromResult(Matches(exchange));
}

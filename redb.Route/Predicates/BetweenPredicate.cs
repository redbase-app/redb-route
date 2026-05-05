using redb.Route.Abstractions;

namespace redb.Route.Predicates;

/// <summary>
/// Predicate that checks whether an expression result falls between two values (inclusive).
/// </summary>
public class BetweenPredicate : IPredicate
{
    private readonly IExpression _expression;
    private readonly IComparable _low;
    private readonly IComparable _high;

    /// <summary>
    /// Initializes a new instance with the specified expression and boundary values.
    /// </summary>
    /// <param name="expression">The expression to evaluate.</param>
    /// <param name="low">The lower bound (inclusive, must implement <see cref="IComparable"/>).</param>
    /// <param name="high">The upper bound (inclusive, must implement <see cref="IComparable"/>).</param>
    public BetweenPredicate(IExpression expression, object low, object high)
    {
        _expression = expression;
        _low = (IComparable)low;
        _high = (IComparable)high;
    }

    /// <inheritdoc />
    public bool Matches(IExchange exchange)
    {
        var result = _expression.Evaluate<IComparable>(exchange);
        return result?.CompareTo(_low) >= 0 && result?.CompareTo(_high) <= 0;
    }

    /// <inheritdoc />
    public Task<bool> MatchesAsync(IExchange exchange)
        => Task.FromResult(Matches(exchange));
}

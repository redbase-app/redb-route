using redb.Route.Abstractions;

namespace redb.Route.Predicates;

/// <summary>
/// Predicate that checks whether an expression result is <c>null</c>.
/// </summary>
public class IsNullPredicate : IPredicate
{
    private readonly IExpression _expression;

    /// <summary>
    /// Initializes a new instance with the specified expression.
    /// </summary>
    /// <param name="expression">The expression to evaluate.</param>
    public IsNullPredicate(IExpression expression)
    {
        _expression = expression;
    }

    /// <inheritdoc />
    public bool Matches(IExchange exchange)
    {
        var result = _expression.Evaluate<object>(exchange);
        return result == null;
    }

    /// <inheritdoc />
    public Task<bool> MatchesAsync(IExchange exchange)
        => Task.FromResult(Matches(exchange));
}

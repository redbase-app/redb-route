using redb.Route.Abstractions;

namespace redb.Route.Predicates;

/// <summary>
/// Predicate that performs a logical AND between an expression and another predicate.
/// Returns <c>true</c> only when both the expression evaluates to <c>true</c> and the predicate matches.
/// </summary>
public class AndPredicate : IPredicate
{
    private readonly IExpression _expression;
    private readonly IPredicate _predicate;

    /// <summary>
    /// Initializes a new instance combining an expression and a predicate with logical AND.
    /// </summary>
    /// <param name="expression">The expression to evaluate (left-hand side of AND).</param>
    /// <param name="predicate">The predicate to check (right-hand side of AND).</param>
    public AndPredicate(IExpression expression, IPredicate predicate)
    {
        _expression = expression;
        _predicate = predicate;
    }

    /// <inheritdoc />
    public bool Matches(IExchange exchange)
    {
        bool expressionResult = _expression.Evaluate<bool>(exchange);
        return expressionResult && _predicate.Matches(exchange);
    }

    /// <inheritdoc />
    public async Task<bool> MatchesAsync(IExchange exchange)
    {
        bool expressionResult = _expression.Evaluate<bool>(exchange);
        return expressionResult && await _predicate.MatchesAsync(exchange).ConfigureAwait(false);
    }
}

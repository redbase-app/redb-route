using redb.Route.Abstractions;

namespace redb.Route.Predicates;

/// <summary>
/// Predicate that negates the boolean result of an expression (logical NOT).
/// </summary>
public class NotPredicate : IPredicate
{
    private readonly IExpression _expression;

    /// <summary>
    /// Initializes a new instance with the expression to negate.
    /// </summary>
    /// <param name="expression">The expression whose boolean result will be inverted.</param>
    public NotPredicate(IExpression expression)
    {
        _expression = expression ?? throw new ArgumentNullException(nameof(expression));
    }

    /// <inheritdoc />
    public bool Matches(IExchange exchange)
    {
        return !_expression.Evaluate<bool>(exchange);
    }

    /// <inheritdoc />
    public Task<bool> MatchesAsync(IExchange exchange)
    {
        return Task.Run(() =>
        {
            return !_expression.Evaluate<bool>(exchange);
        });
    }
}

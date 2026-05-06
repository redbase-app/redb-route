using redb.Route.Abstractions;

namespace redb.Route.Predicates;

/// <summary>
/// Predicate that checks whether an expression result is contained in a specified set of values.
/// </summary>
public class InPredicate : IPredicate
{
    private readonly IExpression _expression;
    private readonly HashSet<object> _values;

    /// <summary>
    /// Initializes a new instance with the specified expression and allowed values.
    /// </summary>
    /// <param name="expression">The expression to evaluate.</param>
    /// <param name="values">The set of values to check membership against.</param>
    public InPredicate(IExpression expression, params object[] values)
    {
        _expression = expression;
        _values = new HashSet<object>(values);
    }

    /// <inheritdoc />
    public bool Matches(IExchange exchange)
    {
        var result = _expression.Evaluate<object>(exchange);
        return _values.Contains(result);
    }

    /// <inheritdoc />
    public Task<bool> MatchesAsync(IExchange exchange)
        => Task.FromResult(Matches(exchange));
}

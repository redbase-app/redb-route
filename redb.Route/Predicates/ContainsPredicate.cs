using System.Collections;
using redb.Route.Abstractions;

namespace redb.Route.Predicates;

/// <summary>
/// Predicate that checks whether a string contains a substring or a collection contains an element.
/// </summary>
public class ContainsPredicate : IPredicate
{
    private readonly IExpression _expression;
    private readonly object _value;

    /// <summary>
    /// Initializes a new instance with the specified expression and value to search for.
    /// </summary>
    /// <param name="expression">The expression to evaluate.</param>
    /// <param name="value">The substring or element to look for.</param>
    public ContainsPredicate(IExpression expression, object value)
    {
        _expression = expression;
        _value = value;
    }

    /// <inheritdoc />
    public bool Matches(IExchange exchange)
    {
        var result = _expression.Evaluate<object>(exchange);

        if (result is string str && _value is string substr)
            return str.Contains(substr);

        if (result is IEnumerable enumerable)
        {
            foreach (var item in enumerable)
            {
                if (object.Equals(item, _value))
                    return true;
            }
        }

        return false;
    }

    /// <inheritdoc />
    public Task<bool> MatchesAsync(IExchange exchange)
        => Task.FromResult(Matches(exchange));
}

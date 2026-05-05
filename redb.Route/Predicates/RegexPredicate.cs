using System.Text.RegularExpressions;
using redb.Route.Abstractions;

namespace redb.Route.Predicates;

/// <summary>
/// Predicate that checks whether a string expression result matches a regular expression pattern.
/// </summary>
public class RegexPredicate : IPredicate
{
    private readonly IExpression _expression;
    private readonly Regex _regex;

    /// <summary>
    /// Initializes a new instance with the specified expression and regex pattern.
    /// </summary>
    /// <param name="expression">The expression to evaluate.</param>
    /// <param name="pattern">The regular expression pattern to match against.</param>
    public RegexPredicate(IExpression expression, string pattern)
    {
        _expression = expression;
        _regex = new Regex(pattern);
    }

    /// <inheritdoc />
    public bool Matches(IExchange exchange)
    {
        var result = _expression.Evaluate<string>(exchange);
        return result != null && _regex.IsMatch(result);
    }

    /// <inheritdoc />
    public Task<bool> MatchesAsync(IExchange exchange)
        => Task.FromResult(Matches(exchange));
}

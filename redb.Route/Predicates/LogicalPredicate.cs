using redb.Route.Abstractions;
using redb.Route.Expressions;

namespace redb.Route.Predicates;

/// <summary>
/// Predicate that evaluates a string-based logical expression compiled via <see cref="ExpressionResolver"/>.
/// The expression is compiled once at construction time for efficient repeated evaluation.
/// </summary>
public class LogicalPredicate : IPredicate
{
    private readonly string _expression;
    private readonly Func<IExchange, bool> _compiledPredicate;

    /// <summary>
    /// Initializes a new instance with the specified logical expression string.
    /// </summary>
    /// <param name="expression">The logical expression to compile and evaluate.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="expression"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentException">Thrown when the expression cannot be compiled.</exception>
    public LogicalPredicate(string expression)
    {
        _expression = expression ?? throw new ArgumentNullException(nameof(expression));

        try
        {
            // Compile the expression once at predicate creation time
            _compiledPredicate = ExpressionResolver.CompileLogicalPredicate(_expression);
        }
        catch (Exception ex)
        {
            throw new ArgumentException($"Failed to compile logical expression '{_expression}': {ex.Message}", nameof(expression), ex);
        }
    }

    /// <inheritdoc />
    public bool Matches(IExchange exchange)
    {
        try
        {
            // Use the pre-compiled predicate
            return _compiledPredicate(exchange);
        }
        catch
        {
            // Return false on evaluation error
            return false;
        }
    }

    /// <inheritdoc />
    public Task<bool> MatchesAsync(IExchange exchange)
        => Task.FromResult(Matches(exchange));

    /// <summary>
    /// Returns a string representation of this predicate including the expression.
    /// </summary>
    /// <returns>A string in the format <c>LogicalPredicate(expression)</c>.</returns>
    public override string ToString()
    {
        return $"LogicalPredicate({_expression})";
    }
}

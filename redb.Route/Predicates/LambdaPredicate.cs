using redb.Route.Abstractions;

namespace redb.Route.Predicates;

/// <summary>
/// Predicate that wraps a <see cref="Func{IExchange, Boolean}"/> delegate for inline predicate logic.
/// </summary>
public class LambdaPredicate : IPredicate
{
    private readonly Func<IExchange, bool> _predicate;

    /// <summary>
    /// Initializes a new instance with the specified delegate.
    /// </summary>
    /// <param name="predicate">The delegate that evaluates the predicate condition.</param>
    public LambdaPredicate(Func<IExchange, bool> predicate)
    {
        _predicate = predicate ?? throw new ArgumentNullException(nameof(predicate));
    }

    /// <inheritdoc />
    public bool Matches(IExchange exchange)
    {
        return _predicate(exchange);
    }

    /// <inheritdoc />
    public Task<bool> MatchesAsync(IExchange exchange)
        => Task.FromResult(Matches(exchange));
}

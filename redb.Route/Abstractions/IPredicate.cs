namespace redb.Route.Abstractions;

/// <summary>
/// Predicate for conditional message processing in route pipelines.
/// Determines whether a given condition is satisfied for a specific exchange.
/// Used by filter, choice, when, and other routing constructs.
/// </summary>
public interface IPredicate
{
    /// <summary>
    /// Evaluates the predicate synchronously against the exchange.
    /// </summary>
    /// <param name="exchange">The exchange to test.</param>
    /// <returns><c>true</c> if the condition is satisfied; otherwise <c>false</c>.</returns>
    bool Matches(IExchange exchange);

    /// <summary>
    /// Evaluates the predicate asynchronously against the exchange.
    /// </summary>
    /// <param name="exchange">The exchange to test.</param>
    /// <returns>A task yielding <c>true</c> if the condition is satisfied; otherwise <c>false</c>.</returns>
    Task<bool> MatchesAsync(IExchange exchange) => Task.FromResult(Matches(exchange));
}

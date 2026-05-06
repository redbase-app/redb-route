using redb.Route.Abstractions;

namespace redb.Route.Processors;

/// <summary>
/// A single condition-action pair used by <see cref="ChoiceProcessor"/>.
/// When the predicate matches, the associated processor executes.
/// </summary>
public class WhenClause
{
    /// <summary>Predicate to evaluate against the exchange.</summary>
    public Func<IExchange, bool> Predicate { get; }

    /// <summary>Processor to execute when the predicate is true.</summary>
    public IProcessor Processor { get; }

    /// <summary>Creates a when clause with a predicate and action.</summary>
    /// <param name="predicate">The condition to check.</param>
    /// <param name="processor">The processor to run when condition is met.</param>
    public WhenClause(Func<IExchange, bool> predicate, IProcessor processor)
    {
        Predicate = predicate ?? throw new ArgumentNullException(nameof(predicate));
        Processor = processor ?? throw new ArgumentNullException(nameof(processor));
    }
}

/// <summary>
/// Content-based router: evaluates when-clauses in order,
/// executes the first matching branch. Falls through to Otherwise if no match.
/// </summary>
public class ChoiceProcessor : IProcessor
{
    private readonly List<WhenClause> _whenClauses = [];
    private IProcessor? _otherwise;

    /// <summary>Gets the list of when-clauses.</summary>
    public IReadOnlyList<WhenClause> WhenClauses => _whenClauses;

    /// <summary>Gets the fallback processor (executed when no when-clause matches).</summary>
    public IProcessor? Otherwise => _otherwise;

    /// <summary>Adds a when-clause to the choice.</summary>
    /// <param name="predicate">Condition to evaluate.</param>
    /// <param name="processor">Processor to run if condition is true.</param>
    /// <returns>This instance for fluent chaining.</returns>
    public ChoiceProcessor When(Func<IExchange, bool> predicate, IProcessor processor)
    {
        _whenClauses.Add(new WhenClause(predicate, processor));
        return this;
    }

    /// <summary>Sets the otherwise (fallback) processor.</summary>
    /// <param name="processor">Fallback processor.</param>
    /// <returns>This instance for fluent chaining.</returns>
    public ChoiceProcessor SetOtherwise(IProcessor processor)
    {
        _otherwise = processor ?? throw new ArgumentNullException(nameof(processor));
        return this;
    }

    /// <inheritdoc />
    public async Task Process(IExchange exchange, CancellationToken ct = default)
    {
        foreach (var clause in _whenClauses)
        {
            if (clause.Predicate(exchange))
            {
                await clause.Processor.Process(exchange, ct).ConfigureAwait(false);
                return;
            }
        }

        if (_otherwise != null)
        {
            await _otherwise.Process(exchange, ct).ConfigureAwait(false);
        }
    }
}

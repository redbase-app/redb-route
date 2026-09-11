using redb.Route.Abstractions;

namespace redb.Route.Processors;

/// <summary>
/// A single condition-action pair used by <see cref="ChoiceProcessor"/>.
/// When the predicate matches, the associated processor executes.
/// </summary>
public class WhenClause
{
    /// <summary>Predicate to evaluate against the exchange.</summary>
    public Func<IExchange, bool> Predicate => Condition.Matches;

    /// <summary>The condition of this branch, awaited through <see cref="IPredicate.MatchesAsync"/>.</summary>
    public IPredicate Condition { get; }

    /// <summary>Processor to execute when the predicate is true.</summary>
    public IProcessor Processor { get; }

    /// <summary>Creates a when clause with a predicate and action.</summary>
    /// <param name="predicate">The condition to check.</param>
    /// <param name="processor">The processor to run when condition is met.</param>
    public WhenClause(IPredicate condition, IProcessor processor)
    {
        Condition = condition ?? throw new ArgumentNullException(nameof(condition));
        Processor = processor ?? throw new ArgumentNullException(nameof(processor));
    }

    /// <summary>Creates a clause from a delegate, wrapped as a <see cref="Predicates.LambdaPredicate"/>.</summary>
    public WhenClause(Func<IExchange, bool> predicate, IProcessor processor)
        : this(new Predicates.LambdaPredicate(predicate ?? throw new ArgumentNullException(nameof(predicate))), processor)
    {
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

    /// <summary>Adds a When branch guarded by a predicate instance.</summary>
    /// <param name="condition">The condition of the branch.</param>
    /// <param name="processor">The processor to run when the condition holds.</param>
    /// <returns>This processor for fluent chaining.</returns>
    public ChoiceProcessor When(IPredicate condition, IProcessor processor)
    {
        _whenClauses.Add(new WhenClause(condition, processor));
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
            if (await clause.Condition.MatchesAsync(exchange).ConfigureAwait(false))
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

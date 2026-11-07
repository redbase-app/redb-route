using redb.Route.Abstractions;
using redb.Route.Predicates;
using redb.Route.Telemetry;

namespace redb.Route.Processors;

/// <summary>
/// Evaluates a predicate on the exchange and passes through only if true.
/// When predicate returns false, the exchange is skipped (no processing).
/// <para>
/// The predicate is held as an <see cref="IPredicate"/> and awaited through
/// <see cref="IPredicate.MatchesAsync"/>, so a predicate that has to consult a store or a
/// service can do so without blocking. A plain delegate is wrapped in a
/// <see cref="LambdaPredicate"/> at the boundary.
/// </para>
/// </summary>
public class FilterProcessor : IProcessor
{
    private readonly IPredicate _predicate;
    private readonly IProcessor _next;

    /// <summary>Creates a filter processor from a predicate instance.</summary>
    /// <param name="predicate">Predicate that must hold for processing to continue.</param>
    /// <param name="next">The processor to execute when the predicate holds.</param>
    public FilterProcessor(IPredicate predicate, IProcessor next)
    {
        _predicate = predicate ?? throw new ArgumentNullException(nameof(predicate));
        _next = next ?? throw new ArgumentNullException(nameof(next));
    }

    /// <summary>Creates a filter processor from a delegate, wrapped as a <see cref="LambdaPredicate"/>.</summary>
    /// <param name="predicate">Predicate that must return true for processing to continue.</param>
    /// <param name="next">The processor to execute when predicate is true.</param>
    public FilterProcessor(Func<IExchange, bool> predicate, IProcessor next)
        : this(new LambdaPredicate(predicate ?? throw new ArgumentNullException(nameof(predicate))), next)
    {
    }

    /// <inheritdoc />
    public async Task Process(IExchange exchange, CancellationToken ct = default)
    {
        if (await _predicate.MatchesAsync(exchange).ConfigureAwait(false))
        {
            await _next.Process(exchange, ct).ConfigureAwait(false);
            return;
        }

        ProcessorMetrics.FilterDropped.Add(1);
    }
}

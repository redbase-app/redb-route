using redb.Route.Abstractions;
using redb.Route.Telemetry;

namespace redb.Route.Processors;

/// <summary>
/// Evaluates a predicate on the exchange and passes through only if true.
/// When predicate returns false, the exchange is skipped (no processing).
/// </summary>
public class FilterProcessor : IProcessor
{
    private readonly Func<IExchange, bool> _predicate;
    private readonly IProcessor _next;

    /// <summary>Creates a filter processor.</summary>
    /// <param name="predicate">Predicate that must return true for processing to continue.</param>
    /// <param name="next">The processor to execute when predicate is true.</param>
    public FilterProcessor(Func<IExchange, bool> predicate, IProcessor next)
    {
        _predicate = predicate ?? throw new ArgumentNullException(nameof(predicate));
        _next = next ?? throw new ArgumentNullException(nameof(next));
    }

    /// <inheritdoc />
    public Task Process(IExchange exchange, CancellationToken ct = default)
    {
        if (_predicate(exchange))
            return _next.Process(exchange, ct);

        ProcessorMetrics.FilterDropped.Add(1);
        return Task.CompletedTask;
    }
}

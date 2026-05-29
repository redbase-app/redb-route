using redb.Route.Abstractions;
using redb.Route.Telemetry;

namespace redb.Route.Processors;

/// <summary>
/// Aggregates multiple exchanges into a single exchange using a correlation key and completion predicate.
/// Collects exchanges with the same correlation key, then applies the aggregation strategy
/// when the completion predicate is satisfied.
/// </summary>
public class AggregatorProcessor : IProcessor
{
    private readonly Func<IExchange, string> _correlationKey;
    private readonly Func<IExchange, IExchange, IExchange> _aggregationStrategy;
    private readonly Func<IExchange, bool> _completionPredicate;
    private readonly IProcessor _target;

    private readonly Dictionary<string, IExchange> _aggregated = new(StringComparer.Ordinal);
    private readonly object _lock = new();

    /// <summary>Creates an aggregator processor.</summary>
    /// <param name="correlationKey">Function to extract the correlation key from an exchange.</param>
    /// <param name="aggregationStrategy">
    /// Function to merge a new exchange into the existing aggregate.
    /// Receives (oldAggregate, newExchange) and returns the merged exchange.
    /// </param>
    /// <param name="completionPredicate">
    /// Predicate that returns true when aggregation is complete for a given group.
    /// Evaluated after each merge.
    /// </param>
    /// <param name="target">Processor to handle the completed aggregate.</param>
    public AggregatorProcessor(
        Func<IExchange, string> correlationKey,
        Func<IExchange, IExchange, IExchange> aggregationStrategy,
        Func<IExchange, bool> completionPredicate,
        IProcessor target)
    {
        _correlationKey = correlationKey ?? throw new ArgumentNullException(nameof(correlationKey));
        _aggregationStrategy = aggregationStrategy ?? throw new ArgumentNullException(nameof(aggregationStrategy));
        _completionPredicate = completionPredicate ?? throw new ArgumentNullException(nameof(completionPredicate));
        _target = target ?? throw new ArgumentNullException(nameof(target));
    }

    /// <inheritdoc />
    public async Task Process(IExchange exchange, CancellationToken ct = default)
    {
        var key = _correlationKey(exchange);
        IExchange? completed = null;
        var groupCreated = false;

        lock (_lock)
        {
            if (_aggregated.TryGetValue(key, out var existing))
            {
                var merged = _aggregationStrategy(existing, exchange);
                _aggregated[key] = merged;

                if (_completionPredicate(merged))
                {
                    completed = merged;
                    _aggregated.Remove(key);
                }
            }
            else
            {
                _aggregated[key] = exchange;
                groupCreated = true;

                if (_completionPredicate(exchange))
                {
                    completed = exchange;
                    _aggregated.Remove(key);
                    groupCreated = false;
                }
            }
        }

        if (groupCreated)
            ProcessorMetrics.AggregatorInflightGroups.Add(1);

        if (completed != null)
        {
            ProcessorMetrics.AggregatorCompleted.Add(1);
            if (!groupCreated)
                ProcessorMetrics.AggregatorInflightGroups.Add(-1);
            await _target.Process(completed, ct).ConfigureAwait(false);
        }
        // Pre-completion inputs are consumed silently: only completed aggregates flow to
        // _target (the route tail). The aggregator is wired as tail-consuming, so no
        // downstream pipeline steps follow it inside the same PipelineProcessor.
    }

    /// <summary>Gets the number of currently pending aggregation groups.</summary>
    public int PendingGroupCount
    {
        get { lock (_lock) return _aggregated.Count; }
    }
}

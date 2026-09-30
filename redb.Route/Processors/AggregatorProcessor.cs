using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Telemetry;

namespace redb.Route.Processors;

/// <summary>
/// Aggregates multiple exchanges into a single exchange using a correlation key and completion predicate.
/// Collects exchanges with the same correlation key, then applies the aggregation strategy
/// when the completion predicate is satisfied.
/// </summary>
public class AggregatorProcessor : IProcessor, IAsyncDisposable, IStopAwareProcessor
{
    /// <summary>
    /// Apache Camel <c>forceCompletionOnStop</c>: the groups still open when the context stops complete with what they
    /// have. Without it they are dropped, with a warning naming how many.
    /// </summary>
    internal bool ForceCompletionOnStop { get; init; }

    /// <summary>Logs groups dropped or failing to complete on stop.</summary>
    internal ILogger? Logger { get; init; }

    private readonly Func<IExchange, string> _correlationKey;
    private readonly Func<IExchange, IExchange, IExchange> _aggregationStrategy;
    private readonly Func<IExchange, bool> _completionPredicate;
    private readonly IProcessor _target;

    // A group outlives the calls that fed it, so it holds exchanges of its own (Camel copies an exchange into its
    // aggregation repository): every arrival is taken over - its bodies and a way to a DI scope - and all of them are
    // released once the group has gone on down the route, or has been dropped.
    private sealed class Group(IExchange aggregate)
    {
        public IExchange Aggregate { get; set; } = aggregate;
        public List<IExchange> Members { get; } = [aggregate];
    }

    private readonly Dictionary<string, Group> _aggregated = new(StringComparer.Ordinal);
    private readonly object _lock = new();

    // ── Inactivity timeout (Route-XML Ф1.3, Camel completionTimeout parity) ──────────────
    // A group whose last arrival is older than the timeout completes with what it has. The
    // flush rides a periodic scan timer, the same shape the resequencer established: the
    // callback is fire-and-forget, an exception from the target lands on the exchange.
    private readonly TimeSpan? _completionTimeout;
    private readonly Dictionary<string, DateTime> _lastArrival = new(StringComparer.Ordinal);
    private Timer? _timer;
    private readonly CancellationTokenSource _disposeCts = new();
    private int _disposed;

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
        : this(correlationKey, aggregationStrategy, completionPredicate, target, completionTimeout: null)
    {
    }

    /// <summary>
    /// Creates an aggregator with an inactivity timeout: a group that receives nothing for
    /// <paramref name="completionTimeout"/> completes with what it has accumulated (Apache Camel
    /// <c>completionTimeout</c> semantics — the clock restarts on every arrival for the group).
    /// </summary>
    public AggregatorProcessor(
        Func<IExchange, string> correlationKey,
        Func<IExchange, IExchange, IExchange> aggregationStrategy,
        Func<IExchange, bool> completionPredicate,
        IProcessor target,
        TimeSpan? completionTimeout)
    {
        _correlationKey = correlationKey ?? throw new ArgumentNullException(nameof(correlationKey));
        _aggregationStrategy = aggregationStrategy ?? throw new ArgumentNullException(nameof(aggregationStrategy));
        _completionPredicate = completionPredicate ?? throw new ArgumentNullException(nameof(completionPredicate));
        _target = target ?? throw new ArgumentNullException(nameof(target));
        if (completionTimeout is { } t && t <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(completionTimeout), "completionTimeout must be positive.");
        _completionTimeout = completionTimeout;
    }

    /// <inheritdoc />
    public async Task Process(IExchange exchange, CancellationToken ct = default)
    {
        var key = _correlationKey(exchange);
        // The caller disposes its exchange as soon as this returns; the group keeps a copy that owns the bodies.
        var held = exchange is Core.Exchange own ? own.TakeOver() : exchange.Clone();
        Group? completed = null;
        var groupCreated = false;

        lock (_lock)
        {
            if (_aggregated.TryGetValue(key, out var group))
            {
                var merged = _aggregationStrategy(group.Aggregate, held);
                group.Members.Add(held);
                if (!group.Members.Contains(merged))
                    group.Members.Add(merged);
                group.Aggregate = merged;

                if (_completionPredicate(merged))
                {
                    completed = group;
                    RemoveGroup(key);
                }
                else
                {
                    TouchGroup(key);
                }
            }
            else
            {
                group = new Group(held);
                _aggregated[key] = group;
                groupCreated = true;

                if (_completionPredicate(held))
                {
                    completed = group;
                    RemoveGroup(key);
                    groupCreated = false;
                }
                else
                {
                    TouchGroup(key);
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
            // Completed by this arrival: a failure of the completed group is this call's failure too, as it was when
            // the group went on with the caller's own exchange.
            var aggregate = await CompleteAsync(completed, ct).ConfigureAwait(false);
            if (aggregate.Exception is { } failure && !aggregate.ExceptionHandled)
                exchange.Exception ??= failure;
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

    /// <summary>
    /// Sends a completed group on down the route in a DI scope of its own, then releases every exchange it held.
    /// Returns the aggregate, released; its outcome stays readable.
    /// </summary>
    private async Task<IExchange> CompleteAsync(Group group, CancellationToken ct)
    {
        var aggregate = group.Aggregate;
        (aggregate as Core.Exchange)?.EnsureOwnScope();
        try
        {
            await _target.Process(aggregate, ct).ConfigureAwait(false);
        }
        finally
        {
            await ReleaseAsync(group).ConfigureAwait(false);
        }
        return aggregate;
    }

    private static async Task ReleaseAsync(Group group)
    {
        foreach (var member in group.Members)
            await member.DisposeAsync().ConfigureAwait(false);
    }

    // ── Inactivity-timeout plumbing ──────────────────────────────────────────

    /// <summary>Under <see cref="_lock"/>: stamps the group's last arrival and arms the scan timer.</summary>
    private void TouchGroup(string key)
    {
        if (_completionTimeout is not { } timeout)
            return;
        _lastArrival[key] = DateTime.UtcNow;
        if (_timer is null && Volatile.Read(ref _disposed) == 0)
        {
            // Periodic scan at half the timeout (floor 50 ms): expiry detection is at most half a
            // period late, and an idle aggregator costs two no-op callbacks per timeout.
            var period = TimeSpan.FromMilliseconds(Math.Max(timeout.TotalMilliseconds / 2, 50));
            _timer = new Timer(OnScanTimer, null, period, period);
        }
    }

    /// <summary>Under <see cref="_lock"/>: removes the group and its arrival stamp.</summary>
    private void RemoveGroup(string key)
    {
        _aggregated.Remove(key);
        _lastArrival.Remove(key);
    }

    private void OnScanTimer(object? state)
    {
        // Timer callback — flush expired groups on the thread pool. Fire-and-forget is the
        // resequencer's established shape: an exception from the target lands on the exchange.
        _ = FlushExpired(_disposeCts.Token);
    }

    private async Task FlushExpired(CancellationToken ct)
    {
        if (_completionTimeout is not { } timeout)
            return;

        List<Group>? expired = null;
        lock (_lock)
        {
            var cutoff = DateTime.UtcNow - timeout;
            List<string>? keys = null;
            foreach (var (key, last) in _lastArrival)
            {
                if (last > cutoff) continue;
                (keys ??= []).Add(key);
            }
            if (keys is not null)
            {
                expired = new List<Group>(keys.Count);
                foreach (var key in keys)
                {
                    expired.Add(_aggregated[key]);
                    RemoveGroup(key);
                }
            }
        }

        if (expired is null)
            return;

        foreach (var group in expired)
        {
            if (ct.IsCancellationRequested)
            {
                await ReleaseAsync(group).ConfigureAwait(false);
                continue;
            }
            ProcessorMetrics.AggregatorCompleted.Add(1);
            ProcessorMetrics.AggregatorInflightGroups.Add(-1);
            try
            {
                await CompleteAsync(group, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                group.Aggregate.Exception = ex;
                Logger?.LogError(ex, "Completing an aggregation group on timeout failed");
            }
        }
    }

    /// <inheritdoc />
    async Task IStopAwareProcessor.OnConsumersStoppedAsync(CancellationToken ct)
    {
        var groups = TakeAllGroups();
        if (groups.Count == 0)
            return;

        if (!ForceCompletionOnStop)
        {
            Logger?.LogWarning(
                "{Count} aggregation group(s) not yet complete were dropped on stop; set forceCompletionOnStop to complete them.",
                groups.Count);
            await ReleaseAsync(groups).ConfigureAwait(false);
            return;
        }

        foreach (var group in groups)
        {
            ProcessorMetrics.AggregatorCompleted.Add(1);
            try
            {
                await CompleteAsync(group, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                group.Aggregate.Exception = ex;
                Logger?.LogError(ex, "Completing an aggregation group on stop failed");
            }
        }
    }

    /// <summary>Removes every open group, for stop and dispose.</summary>
    private List<Group> TakeAllGroups()
    {
        List<Group> groups;
        lock (_lock)
        {
            groups = [.. _aggregated.Values];
            _aggregated.Clear();
            _lastArrival.Clear();
        }
        if (groups.Count > 0)
            ProcessorMetrics.AggregatorInflightGroups.Add(-groups.Count);
        return groups;
    }

    private static async Task ReleaseAsync(List<Group> groups)
    {
        foreach (var group in groups)
            await ReleaseAsync(group).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        await _disposeCts.CancelAsync().ConfigureAwait(false);
        if (_timer is not null)
            await _timer.DisposeAsync().ConfigureAwait(false);
        _disposeCts.Dispose();

        // A context disposed without a stop still holds its groups: they are dropped, not silently.
        var groups = TakeAllGroups();
        if (groups.Count > 0)
        {
            Logger?.LogWarning("{Count} aggregation group(s) not yet complete were dropped when the aggregator was disposed.",
                groups.Count);
            await ReleaseAsync(groups).ConfigureAwait(false);
        }
    }
}

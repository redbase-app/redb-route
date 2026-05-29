using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Telemetry;

namespace redb.Route.Processors;

/// <summary>
/// Splits a single exchange into multiple exchanges based on a splitter function.
/// Each resulting part is processed individually by the inner processor.
/// Supports parallel processing, aggregation strategy, stopOnException, timeout,
/// and configurable concurrency — matching the full feature set of lt.Core.Route.
/// </summary>
public class SplitterProcessor : IProcessor
{
    private readonly Func<IExchange, IEnumerable<object?>> _splitter;
    private readonly IProcessor _target;
    private readonly bool _parallelProcessing;
    private readonly int _maxDegreeOfParallelism;
    private readonly Func<IExchange?, IExchange, IExchange>? _aggregationStrategy;
    private readonly bool _stopOnException;
    private readonly TimeSpan _timeout;
    private readonly bool _parallelAggregate;
    private readonly bool _aggregateOnException;

    /// <summary>Creates a splitter processor.</summary>
    /// <param name="splitter">Function that splits the exchange body into parts.</param>
    /// <param name="target">Processor to handle each split exchange.</param>
    /// <param name="parallelProcessing">Whether to process parts in parallel.</param>
    /// <param name="maxDegreeOfParallelism">Maximum concurrent tasks when parallel (0 = unlimited).</param>
    /// <param name="aggregationStrategy">
    /// Optional pair-wise aggregation: <c>(oldExchange, newExchange) → merged</c>.
    /// <para>
    /// <b>Camel-compatible contract:</b> for the first part <paramref name="aggregationStrategy"/> is invoked
    /// with <c>oldExchange == null</c>, so the strategy can perform a seed/wrap (e.g. wrap into a list, set a marker header).
    /// Strategies must explicitly handle the null-old case — there is no implicit assign-first fallback.
    /// </para>
    /// </param>
    /// <param name="stopOnException">Whether to stop on the first exception.</param>
    /// <param name="timeout">Timeout for the entire split operation (TimeSpan.Zero = no timeout).</param>
    /// <param name="parallelAggregate">
    /// When <c>true</c> together with <paramref name="parallelProcessing"/>, the aggregation strategy is invoked
    /// inline from each worker task under an internal lock instead of by a single deterministic post-pass.
    /// The strategy MUST be thread-safe. Order of pair-wise calls is non-deterministic.
    /// </param>
    /// <param name="aggregateOnException">
    /// When <c>true</c>, split exchanges that failed (target threw) are still fed into the aggregation
    /// strategy. Useful for collecting error info into the aggregated result. Default <c>false</c>
    /// preserves Camel default (failed parts excluded from aggregate).
    /// </param>
    public SplitterProcessor(
        Func<IExchange, IEnumerable<object?>> splitter,
        IProcessor target,
        bool parallelProcessing = false,
        int maxDegreeOfParallelism = 0,
        Func<IExchange?, IExchange, IExchange>? aggregationStrategy = null,
        bool stopOnException = true,
        TimeSpan timeout = default,
        bool parallelAggregate = false,
        bool aggregateOnException = false)
    {
        _splitter = splitter ?? throw new ArgumentNullException(nameof(splitter));
        _target = target ?? throw new ArgumentNullException(nameof(target));
        _parallelProcessing = parallelProcessing;
        _maxDegreeOfParallelism = maxDegreeOfParallelism > 0 ? maxDegreeOfParallelism : Environment.ProcessorCount;
        _aggregationStrategy = aggregationStrategy;
        _stopOnException = stopOnException;
        _timeout = timeout;
        _parallelAggregate = parallelAggregate;
        _aggregateOnException = aggregateOnException;
    }

    /// <inheritdoc />
    public async Task Process(IExchange exchange, CancellationToken ct = default)
    {
        var parts = _splitter(exchange).ToArray();
        if (parts.Length == 0) return;

        ProcessorMetrics.SplitterParts.Add(parts.Length);

        using var timeoutCts = _timeout > TimeSpan.Zero
            ? CancellationTokenSource.CreateLinkedTokenSource(ct)
            : null;

        if (timeoutCts != null)
            timeoutCts.CancelAfter(_timeout);

        var effectiveCt = timeoutCts?.Token ?? ct;

        try
        {
            if (_parallelProcessing)
                await ProcessParallel(exchange, parts, effectiveCt).ConfigureAwait(false);
            else
                await ProcessSequential(exchange, parts, effectiveCt).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutCts != null && timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new TimeoutException($"Split processing timed out after {_timeout.TotalMilliseconds} ms.");
        }
    }

    private async Task ProcessSequential(IExchange exchange, object?[] parts, CancellationToken ct)
    {
        IExchange? aggregated = null;
        Exception? firstException = null;
        var splitExchanges = new List<IExchange>(parts.Length);

        try
        {
            for (var i = 0; i < parts.Length; i++)
            {
                ct.ThrowIfCancellationRequested();
                if (exchange.IsStopped) break;

                var splitExchange = CreateSplitExchange(exchange, parts[i], i, parts.Length, linked: true);
                splitExchanges.Add(splitExchange);

                try
                {
                    await _target.Process(splitExchange, ct).ConfigureAwait(false);

                    if (_aggregationStrategy != null)
                    {
                        // S1: Camel-compatible — strategy is invoked even for the first part
                        // with oldExchange == null so it can perform a seed/wrap.
                        aggregated = _aggregationStrategy(aggregated, splitExchange);
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    firstException ??= ex;

                    // S4: aggregateOnException — include failed split exchange into aggregate
                    if (_aggregateOnException && _aggregationStrategy != null)
                    {
                        aggregated = _aggregationStrategy(aggregated, splitExchange);
                    }

                    if (_stopOnException)
                    {
                        exchange.Exception = ex;
                        break;
                    }
                }
            }

            // S2: when stopOnException fired, do NOT publish the partial aggregate to parent
            if (!(_stopOnException && firstException != null))
                ApplyAggregation(exchange, aggregated);
        }
        finally
        {
            foreach (var se in splitExchanges)
                if (se is IAsyncDisposable d) await d.DisposeAsync().ConfigureAwait(false);
        }

        if (firstException != null && _stopOnException)
            throw firstException;
    }

    private async Task ProcessParallel(IExchange exchange, object?[] parts, CancellationToken ct)
    {
        var results = new IExchange?[parts.Length];
        var exceptions = new Exception?[parts.Length];
        var shouldStop = 0; // Interlocked flag

        // S3: state for in-task ("parallel") aggregation under lock
        var aggLock = new object();
        IExchange? parallelAggregated = null;

        using var semaphore = new SemaphoreSlim(_maxDegreeOfParallelism);

        var splitExchanges = new IExchange?[parts.Length];
        var tasks = new Task[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            var idx = i;
            tasks[idx] = Task.Run(async () =>
            {
                if (Volatile.Read(ref shouldStop) == 1) return;

                await semaphore.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    if (Volatile.Read(ref shouldStop) == 1) return;

                    var splitExchange = CreateSplitExchange(exchange, parts[idx], idx, parts.Length);
                    splitExchanges[idx] = splitExchange;

                    var succeeded = false;
                    try
                    {
                        await _target.Process(splitExchange, ct).ConfigureAwait(false);
                        results[idx] = splitExchange;
                        succeeded = true;
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        exceptions[idx] = ex;
                        if (_stopOnException)
                        {
                            Interlocked.Exchange(ref shouldStop, 1);
                        }
                    }
                    finally
                    {
                        // S3 + S4: when parallelAggregate is enabled, aggregate inline under lock
                        if (_parallelAggregate && _aggregationStrategy != null
                            && (succeeded || _aggregateOnException))
                        {
                            lock (aggLock)
                            {
                                parallelAggregated = _aggregationStrategy(parallelAggregated, splitExchange);
                            }
                        }

                        // Release DI scopes early; body/headers stay alive for aggregation
                        await splitExchange.ReleaseScopes().ConfigureAwait(false);
                    }
                }
                finally
                {
                    semaphore.Release();
                }
            }, ct);
        }

        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);

            var allExceptions = exceptions.Where(e => e != null).Cast<Exception>().ToArray();
            var hasErrors = allExceptions.Length > 0;

            // S2: do not apply partial aggregate when stopOnException fired
            if (_aggregationStrategy != null && !(_stopOnException && hasErrors))
            {
                if (_parallelAggregate)
                {
                    ApplyAggregation(exchange, parallelAggregated);
                }
                else
                {
                    // Deterministic post-pass aggregation (in index order)
                    IExchange? aggregated = null;
                    for (var i = 0; i < results.Length; i++)
                    {
                        var part = results[i];
                        if (part == null)
                        {
                            // S4: include failed split exchanges if requested
                            if (_aggregateOnException && exceptions[i] != null && splitExchanges[i] != null)
                                part = splitExchanges[i];
                            else
                                continue;
                        }
                        // S1: strategy invoked for the first part with oldExchange == null
                        aggregated = _aggregationStrategy(aggregated, part);
                    }
                    ApplyAggregation(exchange, aggregated);
                }
            }

            if (hasErrors)
            {
                exchange.Exception = allExceptions.Length == 1
                    ? allExceptions[0]
                    : new AggregateException("Multiple split targets failed.", allExceptions);

                if (_stopOnException)
                    throw allExceptions[0];
            }
        }
        finally
        {
            foreach (var se in splitExchanges)
                if (se is IAsyncDisposable d) await d.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static IExchange CreateSplitExchange(IExchange original, object? part, int index, int total, bool linked = false)
    {
        var splitMessage = new Message(part);

        // Copy headers from original
        foreach (var header in original.In.Headers)
            splitMessage.Headers[header.Key] = header.Value;

        // Set split metadata headers
        splitMessage.Headers["CamelSplitIndex"] = index;
        splitMessage.Headers["CamelSplitSize"] = total;
        splitMessage.Headers["CamelSplitComplete"] = index == total - 1;

        return linked ? original.CreateLinkedChild(splitMessage) : original.CreateChild(splitMessage);
    }

    private static void ApplyAggregation(IExchange original, IExchange? aggregated)
    {
        if (aggregated == null) return;

        original.In.Body = aggregated.In.Body;

        // Copy headers from aggregated result
        foreach (var header in aggregated.In.Headers)
            original.In.Headers[header.Key] = header.Value;

        // Copy properties from aggregated result
        foreach (var prop in aggregated.Properties)
            original.Properties[prop.Key] = prop.Value;
    }
}

using redb.Route.Abstractions;

namespace redb.Route.Processors;

/// <summary>
/// Sends the exchange to multiple processors (fan-out).
/// Supports parallel/sequential execution, aggregation strategy, stopOnException,
/// timeout, and configurable concurrency with deterministic result ordering.
/// </summary>
public class MulticastProcessor : IProcessor
{
    private readonly List<IProcessor> _targets = [];
    private readonly bool _parallelProcessing;
    private readonly int _maxDegreeOfParallelism;
    private readonly bool _stopOnException;
    private readonly TimeSpan _timeout;
    private readonly Func<IExchange, IExchange, IExchange>? _aggregationStrategy;

    /// <summary>Gets the list of target processors.</summary>
    public IReadOnlyList<IProcessor> Targets => _targets;

    /// <summary>Creates a multicast processor.</summary>
    /// <param name="parallelProcessing">Whether to process targets in parallel (true) or sequentially (false).</param>
    /// <param name="aggregationStrategy">Optional pair-wise aggregation: (aggregated, current) → merged.</param>
    /// <param name="stopOnException">Whether to stop on the first exception.</param>
    /// <param name="timeout">Timeout for the entire multicast operation (TimeSpan.Zero = no timeout).</param>
    /// <param name="maxDegreeOfParallelism">Maximum concurrent tasks when parallel (0 = unlimited).</param>
    public MulticastProcessor(
        bool parallelProcessing = true,
        Func<IExchange, IExchange, IExchange>? aggregationStrategy = null,
        bool stopOnException = false,
        TimeSpan timeout = default,
        int maxDegreeOfParallelism = 0)
    {
        _parallelProcessing = parallelProcessing;
        _aggregationStrategy = aggregationStrategy;
        _stopOnException = stopOnException;
        _timeout = timeout;
        _maxDegreeOfParallelism = maxDegreeOfParallelism > 0 ? maxDegreeOfParallelism : Environment.ProcessorCount;
    }

    /// <summary>Adds a target processor to the multicast.</summary>
    /// <param name="processor">Target processor.</param>
    /// <returns>This instance for fluent chaining.</returns>
    public MulticastProcessor AddTarget(IProcessor processor)
    {
        ArgumentNullException.ThrowIfNull(processor);
        _targets.Add(processor);
        return this;
    }

    /// <inheritdoc />
    public async Task Process(IExchange exchange, CancellationToken ct = default)
    {
        if (_targets.Count == 0) return;

        using var timeoutCts = _timeout > TimeSpan.Zero
            ? CancellationTokenSource.CreateLinkedTokenSource(ct)
            : null;

        if (timeoutCts != null)
            timeoutCts.CancelAfter(_timeout);

        var effectiveCt = timeoutCts?.Token ?? ct;

        try
        {
            if (_parallelProcessing)
                await ProcessParallel(exchange, effectiveCt).ConfigureAwait(false);
            else
                await ProcessSequential(exchange, effectiveCt).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutCts != null && timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new TimeoutException($"Multicast processing timed out after {_timeout.TotalMilliseconds} ms.");
        }
    }

    private async Task ProcessSequential(IExchange exchange, CancellationToken ct)
    {
        var clones = new IExchange[_targets.Count];
        Exception? firstException = null;

        try
        {
            for (var i = 0; i < _targets.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                clones[i] = exchange.CloneLinked();

                try
                {
                    await _targets[i].Process(clones[i], ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    clones[i].Exception = ex;
                    firstException ??= ex;

                    if (_stopOnException)
                    {
                        exchange.Exception = ex;
                        break;
                    }
                }
            }

            Aggregate(exchange, clones);
        }
        finally
        {
            foreach (var clone in clones)
                if (clone is IAsyncDisposable d) await d.DisposeAsync().ConfigureAwait(false);
        }

        if (firstException != null && _stopOnException)
            throw firstException;
    }

    private async Task ProcessParallel(IExchange exchange, CancellationToken ct)
    {
        var clones = new IExchange?[_targets.Count];
        var exceptions = new Exception?[_targets.Count];
        var shouldStop = 0;

        using var semaphore = new SemaphoreSlim(_maxDegreeOfParallelism);

        try
        {
            var tasks = new Task[_targets.Count];
            for (var i = 0; i < _targets.Count; i++)
            {
                var idx = i;

                tasks[idx] = Task.Run(async () =>
                {
                    if (Volatile.Read(ref shouldStop) == 1) return;

                    await semaphore.WaitAsync(ct).ConfigureAwait(false);
                    try
                    {
                        if (Volatile.Read(ref shouldStop) == 1) return;

                        var clone = exchange.Clone();
                        clones[idx] = clone;

                        try
                        {
                            await _targets[idx].Process(clone, ct).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            clone.Exception = ex;
                            exceptions[idx] = ex;
                            if (_stopOnException)
                            {
                                Interlocked.Exchange(ref shouldStop, 1);
                            }
                        }
                        finally
                        {
                            // Release DI scopes early; body/headers stay alive for aggregation
                            await clone.ReleaseScopes().ConfigureAwait(false);
                        }
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                }, ct);
            }

            await Task.WhenAll(tasks).ConfigureAwait(false);

            // Aggregate in deterministic index order
            Aggregate(exchange, clones!);

            var allExceptions = exceptions.Where(e => e != null).Cast<Exception>().ToArray();
            if (allExceptions.Length > 0)
            {
                exchange.Exception = allExceptions.Length == 1
                    ? allExceptions[0]
                    : new AggregateException("Multiple multicast targets failed.", allExceptions);

                if (_stopOnException)
                    throw allExceptions[0];
            }
        }
        finally
        {
            foreach (var clone in clones)
                if (clone is IAsyncDisposable d) await d.DisposeAsync().ConfigureAwait(false);
        }
    }

    private void Aggregate(IExchange original, IExchange[] clones)
    {
        if (_aggregationStrategy == null) return;

        IExchange? aggregated = null;
        foreach (var clone in clones)
        {
            if (clone == null) continue;
            // Skip error clones if stopOnException is set — only aggregate successful results
            if (_stopOnException && clone.Exception != null) continue;

            aggregated = aggregated == null
                ? clone
                : _aggregationStrategy(aggregated, clone);
        }

        if (aggregated != null)
        {
            original.In.Body = aggregated.In.Body;
            foreach (var header in aggregated.In.Headers)
                original.In.Headers[header.Key] = header.Value;
            foreach (var prop in aggregated.Properties)
                original.Properties[prop.Key] = prop.Value;
        }
    }
}

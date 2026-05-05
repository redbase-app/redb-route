using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.Core;

namespace redb.Route.Processors;

/// <summary>
/// Processor for the Scatter-Gather EIP pattern.
/// Sends the exchange to multiple endpoints (scatter phase), collects all responses,
/// and aggregates them using a mandatory pair-wise strategy (gather phase).
/// Supports static and dynamic recipient lists, parallel/sequential processing,
/// timeout with partial-result aggregation, and configurable error handling.
/// </summary>
public sealed class ScatterGatherProcessor : IProcessor, IAsyncDisposable
{
    private readonly IRouteContext _context;
    private readonly IReadOnlyList<string>? _staticRecipients;
    private readonly Func<IExchange, IEnumerable<string>>? _dynamicRecipients;
    private readonly Func<IExchange, IExchange, IExchange> _aggregationStrategy;
    private readonly TimeSpan _timeout;
    private readonly bool _parallelProcessing;
    private readonly bool _stopOnException;
    private readonly int _maxDegreeOfParallelism;
    private readonly ILogger? _logger;
    private readonly ConcurrentDictionary<string, Lazy<ToProcessor>> _producerCache = new(StringComparer.Ordinal);

    /// <summary>
    /// Creates a scatter-gather processor with static recipients.
    /// </summary>
    /// <param name="context">Route context for endpoint resolution.</param>
    /// <param name="recipients">Fixed list of endpoint URIs to scatter to.</param>
    /// <param name="aggregationStrategy">Pair-wise aggregation: (accumulated, current) → merged.</param>
    /// <param name="parallelProcessing">Whether to send to all recipients in parallel.</param>
    /// <param name="maxDegreeOfParallelism">Max concurrent sends (0 = processor count).</param>
    /// <param name="stopOnException">Whether to stop on the first error (true) or aggregate partial results (false).</param>
    /// <param name="timeout">Timeout for the entire operation (TimeSpan.Zero = no timeout).</param>
    /// <param name="logger">Optional logger.</param>
    public ScatterGatherProcessor(
        IRouteContext context,
        IReadOnlyList<string> recipients,
        Func<IExchange, IExchange, IExchange> aggregationStrategy,
        bool parallelProcessing = true,
        int maxDegreeOfParallelism = 0,
        bool stopOnException = false,
        TimeSpan timeout = default,
        ILogger? logger = null)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        ArgumentNullException.ThrowIfNull(recipients);
        if (recipients.Count == 0)
            throw new ArgumentException("At least one recipient is required.", nameof(recipients));
        _staticRecipients = recipients;
        _dynamicRecipients = null;
        _aggregationStrategy = aggregationStrategy ?? throw new ArgumentNullException(nameof(aggregationStrategy));
        _parallelProcessing = parallelProcessing;
        _maxDegreeOfParallelism = maxDegreeOfParallelism;
        _stopOnException = stopOnException;
        _timeout = timeout >= TimeSpan.Zero ? timeout : throw new ArgumentOutOfRangeException(nameof(timeout));
        _logger = logger;
    }

    /// <summary>
    /// Creates a scatter-gather processor with dynamic recipients resolved at runtime.
    /// </summary>
    /// <param name="context">Route context for endpoint resolution.</param>
    /// <param name="recipientFactory">Function that returns target URIs from the exchange.</param>
    /// <param name="aggregationStrategy">Pair-wise aggregation: (accumulated, current) → merged.</param>
    /// <param name="parallelProcessing">Whether to send to all recipients in parallel.</param>
    /// <param name="maxDegreeOfParallelism">Max concurrent sends (0 = processor count).</param>
    /// <param name="stopOnException">Whether to stop on the first error (true) or aggregate partial results (false).</param>
    /// <param name="timeout">Timeout for the entire operation (TimeSpan.Zero = no timeout).</param>
    /// <param name="logger">Optional logger.</param>
    public ScatterGatherProcessor(
        IRouteContext context,
        Func<IExchange, IEnumerable<string>> recipientFactory,
        Func<IExchange, IExchange, IExchange> aggregationStrategy,
        bool parallelProcessing = true,
        int maxDegreeOfParallelism = 0,
        bool stopOnException = false,
        TimeSpan timeout = default,
        ILogger? logger = null)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _staticRecipients = null;
        _dynamicRecipients = recipientFactory ?? throw new ArgumentNullException(nameof(recipientFactory));
        _aggregationStrategy = aggregationStrategy ?? throw new ArgumentNullException(nameof(aggregationStrategy));
        _parallelProcessing = parallelProcessing;
        _maxDegreeOfParallelism = maxDegreeOfParallelism;
        _stopOnException = stopOnException;
        _timeout = timeout >= TimeSpan.Zero ? timeout : throw new ArgumentOutOfRangeException(nameof(timeout));
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task Process(IExchange exchange, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(exchange);

        IReadOnlyList<string> recipients = _staticRecipients
            ?? (IReadOnlyList<string>)_dynamicRecipients!(exchange).ToList();

        if (recipients.Count == 0)
            throw new InvalidOperationException("Scatter-gather requires at least one recipient.");

        _logger?.LogDebug("Scatter-gather: dispatching to {Count} recipients", recipients.Count);

        using var timeoutCts = _timeout > TimeSpan.Zero
            ? CancellationTokenSource.CreateLinkedTokenSource(ct)
            : null;
        timeoutCts?.CancelAfter(_timeout);
        var effectiveCt = timeoutCts?.Token ?? ct;

        try
        {
            if (_parallelProcessing)
                await ProcessParallel(exchange, recipients, effectiveCt, ct).ConfigureAwait(false);
            else
                await ProcessSequential(exchange, recipients, effectiveCt, ct).ConfigureAwait(false);
        }
        // Wraps timeout-only OCE into TimeoutException.
        // In best-effort mode, timeout is handled internally (partial results aggregated)
        // so this catch only fires in stopOnException mode.
        catch (OperationCanceledException) when (_timeout > TimeSpan.Zero && !ct.IsCancellationRequested)
        {
            // Timeout reached with stopOnException — wrap in TimeoutException
            throw new TimeoutException($"Scatter-gather timed out after {_timeout}.");
        }
    }

    private async Task ProcessSequential(
        IExchange exchange, IReadOnlyList<string> recipients,
        CancellationToken ct, CancellationToken callerCt)
    {
        IExchange? aggregated = null;
        var clones = new List<IExchange>(recipients.Count);

        try
        {
            foreach (var uri in recipients)
            {
                ct.ThrowIfCancellationRequested();
                var clone = exchange.CloneLinked();
                clones.Add(clone);

                try
                {
                    var producer = GetOrCreateProducer(uri);
                    await producer.Process(clone, ct).ConfigureAwait(false);
                    aggregated = aggregated is null ? clone : _aggregationStrategy(aggregated, clone);

                    _logger?.LogDebug("Scatter-gather: received response from {Endpoint}", uri);
                }
                catch (OperationCanceledException) when (!callerCt.IsCancellationRequested && !_stopOnException)
                {
                    // Timeout in best-effort mode — aggregate partial results collected so far
                    _logger?.LogWarning(
                        "Scatter-gather: timed out, aggregating partial results");
                    break;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    if (_stopOnException) throw;
                    clone.Exception = ex;
                    _logger?.LogWarning(ex, "Scatter-gather: endpoint {Endpoint} failed", uri);
                    aggregated = aggregated is null ? clone : _aggregationStrategy(aggregated, clone);
                }
            }

            ApplyAggregation(exchange, aggregated);
        }
        finally
        {
            foreach (var clone in clones)
                if (clone is IAsyncDisposable d)
                    await d.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task ProcessParallel(
        IExchange exchange, IReadOnlyList<string> recipients,
        CancellationToken ct, CancellationToken callerCt)
    {
        var clones = new IExchange?[recipients.Count];

        try
        {
            var maxDop = _maxDegreeOfParallelism > 0
                ? _maxDegreeOfParallelism
                : Environment.ProcessorCount;
            using var semaphore = new SemaphoreSlim(maxDop);

            async Task<IExchange> SendToRecipient(int index)
            {
                await semaphore.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    var clone = exchange.Clone();
                    clones[index] = clone;
                    var producer = GetOrCreateProducer(recipients[index]);
                    await producer.Process(clone, ct).ConfigureAwait(false);

                    _logger?.LogDebug("Scatter-gather: received response from {Endpoint}", recipients[index]);
                    return clone;
                }
                finally
                {
                    // Release DI scopes early; body/headers stay alive for aggregation
                    if (clones[index] != null)
                        await clones[index]!.ReleaseScopes().ConfigureAwait(false);
                    semaphore.Release();
                }
            }

            var tasks = Enumerable.Range(0, recipients.Count)
                .Select(SendToRecipient)
                .ToList();

            IExchange?[] results;
            if (_stopOnException)
            {
                results = await Task.WhenAll(tasks).ConfigureAwait(false);
            }
            else
            {
                // Best-effort: collect completed results, handle failures/timeouts gracefully
                results = await Task.WhenAll(tasks.Select(async (t, index) =>
                {
                    try
                    {
                        return await t.ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!callerCt.IsCancellationRequested)
                    {
                        // Timeout — skip this result
                        _logger?.LogWarning("Scatter-gather: endpoint {Endpoint} timed out", recipients[index]);
                        return null;
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        var clone = clones[index] ?? exchange.Clone();
                        clones[index] = clone;
                        clone.Exception = ex;
                        _logger?.LogWarning(ex, "Scatter-gather: endpoint {Endpoint} failed", recipients[index]);
                        return clone;
                    }
                })).ConfigureAwait(false);
            }

            // Aggregate in deterministic index order
            IExchange? aggregated = null;
            foreach (var result in results)
            {
                if (result == null) continue;
                aggregated = aggregated is null ? result : _aggregationStrategy(aggregated, result);
            }

            ApplyAggregation(exchange, aggregated);
        }
        finally
        {
            foreach (var clone in clones)
                if (clone is IAsyncDisposable d)
                    await d.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Copies aggregated body, headers, and properties back to the original exchange.
    /// </summary>
    private static void ApplyAggregation(IExchange original, IExchange? aggregated)
    {
        if (aggregated == null) return;
        original.In.Body = aggregated.In.Body;
        foreach (var header in aggregated.In.Headers)
            original.In.Headers[header.Key] = header.Value;
        foreach (var prop in aggregated.Properties)
            original.Properties[prop.Key] = prop.Value;
    }

    private ToProcessor GetOrCreateProducer(string endpoint)
    {
        return _producerCache.GetOrAdd(endpoint,
            ep => new Lazy<ToProcessor>(() => new ToProcessor(ep, _context))).Value;
    }

    /// <summary>
    /// Stops all cached producers and clears the cache.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        List<Exception>? errors = null;
        foreach (var lazy in _producerCache.Values)
        {
            if (lazy.IsValueCreated)
            {
                try
                {
                    await lazy.Value.StopProducerAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    errors ??= [];
                    errors.Add(ex);
                }
            }
        }

        _producerCache.Clear();

        if (errors != null)
            throw new AggregateException("One or more producers failed to stop.", errors);
    }
}

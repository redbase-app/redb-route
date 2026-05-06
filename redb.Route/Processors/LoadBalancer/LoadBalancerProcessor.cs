using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.Core;

namespace redb.Route.Processors.LoadBalancer;

/// <summary>
/// Processor for the Load Balancer EIP pattern.
/// Selects an endpoint using a pluggable <see cref="ILoadBalancerStrategy"/>,
/// then delegates the exchange to the selected endpoint's producer.
/// Producers are lazily created and cached per endpoint URI (thread-safe).
/// For <see cref="FailoverStrategy"/>, automatically retries with the next healthy endpoint on failure.
/// </summary>
public sealed class LoadBalancerProcessor : IProcessor, IAsyncDisposable
{
    private readonly IRouteContext _context;
    private readonly IReadOnlyList<string> _endpoints;
    private readonly ILoadBalancerStrategy _strategy;
    private readonly ILogger? _logger;
    private readonly ConcurrentDictionary<string, Lazy<ToProcessor>> _producerCache = new(StringComparer.Ordinal);

    /// <summary>
    /// Creates a new load balancer processor.
    /// </summary>
    /// <param name="context">Route context for endpoint resolution.</param>
    /// <param name="endpoints">List of endpoint URIs to balance across.</param>
    /// <param name="strategy">Strategy for selecting the next endpoint.</param>
    /// <param name="logger">Optional logger.</param>
    public LoadBalancerProcessor(
        IRouteContext context,
        IReadOnlyList<string> endpoints,
        ILoadBalancerStrategy strategy,
        ILogger? logger = null)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _endpoints = endpoints ?? throw new ArgumentNullException(nameof(endpoints));
        _strategy = strategy ?? throw new ArgumentNullException(nameof(strategy));
        _logger = logger;

        if (endpoints.Count == 0)
            throw new ArgumentException("At least one endpoint is required.", nameof(endpoints));
    }

    /// <inheritdoc />
    public async Task Process(IExchange exchange, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(exchange);

        if (_strategy is FailoverStrategy)
        {
            await ProcessWithFailover(exchange, ct).ConfigureAwait(false);
            return;
        }

        var selected = _strategy.Select(exchange, _endpoints);
        await SendToEndpoint(selected, exchange, ct).ConfigureAwait(false);
    }

    private async Task ProcessWithFailover(IExchange exchange, CancellationToken ct)
    {
        // Try up to N endpoints (one per attempt)
        for (var attempt = 0; attempt < _endpoints.Count; attempt++)
        {
            var selected = _strategy.Select(exchange, _endpoints);
            try
            {
                var producer = GetOrCreateProducer(selected);
                await producer.Process(exchange, ct).ConfigureAwait(false);
                _strategy.ReportSuccess(selected);

                _logger?.LogDebug("Load balancer: sent to {Endpoint} (failover attempt {Attempt})",
                    selected, attempt + 1);
                return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _strategy.ReportFailure(selected, ex);

                _logger?.LogWarning(ex,
                    "Load balancer: endpoint {Endpoint} failed (attempt {Attempt}/{Total})",
                    selected, attempt + 1, _endpoints.Count);

                // Last attempt — re-throw
                if (attempt == _endpoints.Count - 1)
                    throw;
            }
        }
    }

    private async Task SendToEndpoint(string endpoint, IExchange exchange, CancellationToken ct)
    {
        try
        {
            var producer = GetOrCreateProducer(endpoint);
            await producer.Process(exchange, ct).ConfigureAwait(false);
            _strategy.ReportSuccess(endpoint);

            _logger?.LogDebug("Load balancer: sent to {Endpoint}", endpoint);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _strategy.ReportFailure(endpoint, ex);
            throw;
        }
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
        foreach (var lazy in _producerCache.Values)
        {
            if (lazy.IsValueCreated)
                await lazy.Value.StopProducerAsync().ConfigureAwait(false);
        }

        _producerCache.Clear();
    }
}

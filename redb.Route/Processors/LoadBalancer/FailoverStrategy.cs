using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using redb.Route.Abstractions;

namespace redb.Route.Processors.LoadBalancer;

/// <summary>
/// Failover load balancer strategy.
/// Selects the first healthy endpoint from an ordered list.
/// Tracks failures per endpoint and allows recovery after a configurable timeout.
/// Thread-safe via <see cref="ConcurrentDictionary{TKey,TValue}"/>.
/// </summary>
public sealed class FailoverStrategy : ILoadBalancerStrategy
{
    private readonly int _maxFailures;
    private readonly TimeSpan _recoveryTimeout;
    private readonly ConcurrentDictionary<string, FailureRecord> _failures = new(StringComparer.Ordinal);

    /// <summary>
    /// Creates a failover strategy.
    /// </summary>
    /// <param name="maxFailures">Number of consecutive failures before an endpoint is considered unhealthy.</param>
    /// <param name="recoveryTimeout">Time after which a failed endpoint is retried (probing).</param>
    public FailoverStrategy(int maxFailures = 3, TimeSpan? recoveryTimeout = null)
    {
        _maxFailures = maxFailures > 0 ? maxFailures : throw new ArgumentOutOfRangeException(nameof(maxFailures));
        _recoveryTimeout = recoveryTimeout ?? TimeSpan.FromSeconds(30);

        if (_recoveryTimeout < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(recoveryTimeout), "Recovery timeout must be non-negative.");
    }

    /// <inheritdoc />
    public string Select(IExchange exchange, IReadOnlyList<string> endpoints)
    {
        if (endpoints.Count == 0)
            throw new InvalidOperationException("No endpoints configured for load balancer.");

        foreach (var ep in endpoints)
        {
            if (!_failures.TryGetValue(ep, out var record))
                return ep;

            if (record.Count < _maxFailures)
                return ep;

            // Recovery probe: if enough time passed since last failure, try again
            if (DateTimeOffset.UtcNow - record.LastFailure > _recoveryTimeout)
                return ep;
        }

        // All endpoints failed — return first and let it fail with proper exception
        return endpoints[0];
    }

    /// <inheritdoc />
    public void ReportFailure(string endpoint, Exception exception)
    {
        _failures.AddOrUpdate(
            endpoint,
            _ => new FailureRecord(1, DateTimeOffset.UtcNow),
            (_, old) => new FailureRecord(old.Count + 1, DateTimeOffset.UtcNow));
    }

    /// <inheritdoc />
    public void ReportSuccess(string endpoint)
    {
        _failures.TryRemove(endpoint, out _);
    }

    private sealed record FailureRecord(int Count, DateTimeOffset LastFailure);
}

using System;
using System.Collections.Generic;

namespace redb.Route.Abstractions;

/// <summary>
/// Strategy for selecting a target URI from a list of candidates.
/// Implementations must be thread-safe for concurrent pipeline usage.
/// </summary>
public interface ILoadBalancerStrategy
{
    /// <summary>
    /// Selects the next target URI based on strategy logic.
    /// </summary>
    /// <param name="exchange">Current exchange (for sticky/content-based routing).</param>
    /// <param name="endpoints">Available endpoint URIs.</param>
    /// <returns>Selected URI.</returns>
    string Select(IExchange exchange, IReadOnlyList<string> endpoints);

    /// <summary>
    /// Reports that a selected endpoint has failed.
    /// Used by failover strategies to track unhealthy endpoints.
    /// Default implementation is a no-op.
    /// </summary>
    /// <param name="endpoint">The failed endpoint URI.</param>
    /// <param name="exception">The exception that occurred.</param>
    void ReportFailure(string endpoint, Exception exception) { }

    /// <summary>
    /// Reports that a selected endpoint succeeded.
    /// Used by failover strategies to mark endpoints as healthy again.
    /// Default implementation is a no-op.
    /// </summary>
    /// <param name="endpoint">The successful endpoint URI.</param>
    void ReportSuccess(string endpoint) { }
}

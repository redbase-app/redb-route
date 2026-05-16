using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace redb.Route.Extensions;

/// <summary>
/// Extension methods for adding route engine health checks.
/// </summary>
public static class HealthCheckExtensions
{
    /// <summary>
    /// Adds a health check for the redb.Route engine.
    /// </summary>
    /// <param name="builder">Health checks builder.</param>
    /// <param name="name">Health check name (default: "redb-route").</param>
    /// <param name="failureStatus">
    /// Status to report on failure (default: <see cref="HealthStatus.Degraded"/>).
    /// </param>
    /// <param name="tags">Optional tags for filtering.</param>
    /// <returns>The builder for chaining.</returns>
    public static IHealthChecksBuilder AddRedbRouteCheck(
        this IHealthChecksBuilder builder,
        string name = "redb-route",
        HealthStatus? failureStatus = HealthStatus.Degraded,
        IEnumerable<string>? tags = null)
    {
        return builder.AddCheck<RouteHealthCheck>(name, failureStatus, tags ?? []);
    }
}

#pragma warning disable CS0619
using Microsoft.Extensions.Diagnostics.HealthChecks;
using redb.Route.Abstractions;
using redb.Route.Core;

namespace redb.Route.Extensions;

/// <summary>
/// Health check that reports per-route status and endpoint health.
/// Aggregates route lifecycle state and endpoint statistics into an overall health result.
/// </summary>
public sealed class RouteHealthCheck : IHealthCheck
{
    private readonly IRouteContext _context;

    /// <summary>Creates a route context health check.</summary>
    /// <param name="context">The route context instance.</param>
    public RouteHealthCheck(IRouteContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    /// <inheritdoc />
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var routes = _context.Routes;

        if (routes.Count == 0)
        {
            return Task.FromResult(HealthCheckResult.Degraded(
                "No routes are registered."));
        }

        var overallStatus = HealthStatus.Healthy;
        var routeDetails = new List<object>(routes.Count);

        foreach (var route in routes)
        {
            var endpointHealth = GetEndpointHealth(route);

            routeDetails.Add(new Dictionary<string, object?>
            {
                ["routeId"] = route.RouteId,
                ["fromUri"] = route.FromUri,
                ["status"] = route.Status.ToString(),
                ["endpointHealth"] = endpointHealth?.ToString(),
                ["errors"] = GetErrorCount(route),
                ["lastError"] = GetLastError(route),
            });

            // Route status aggregation
            if (route.Status == RouteStatus.Errored)
            {
                overallStatus = HealthStatus.Unhealthy;
            }
            else if (route.Status is RouteStatus.Stopped or RouteStatus.Suspended or RouteStatus.Suspending or RouteStatus.Stopping
                     && overallStatus != HealthStatus.Unhealthy)
            {
                overallStatus = HealthStatus.Degraded;
            }

            // Endpoint health aggregation
            if (endpointHealth is EndpointHealthStatus.Critical or EndpointHealthStatus.Failed)
            {
                overallStatus = HealthStatus.Unhealthy;
            }
            else if (endpointHealth == EndpointHealthStatus.Warning
                     && overallStatus != HealthStatus.Unhealthy)
            {
                overallStatus = HealthStatus.Degraded;
            }
        }

        var data = new Dictionary<string, object>
        {
            ["routeCount"] = routes.Count,
            ["routes"] = routeDetails,
        };

        var description = overallStatus switch
        {
            HealthStatus.Healthy => $"{routes.Count} route(s) running.",
            HealthStatus.Degraded => $"{routes.Count} route(s), some degraded.",
            _ => $"{routes.Count} route(s), errors detected."
        };

        var result = new HealthCheckResult(overallStatus, description, data: data);
        return Task.FromResult(result);
    }

    private static EndpointHealthStatus? GetEndpointHealth(CompiledRoute route)
    {
        return route.Endpoint is IEndpointStatistics stats ? stats.HealthStatus : null;
    }

    private static long GetErrorCount(CompiledRoute route)
    {
        return route.Endpoint is IEndpointStatistics stats ? stats.Errors : 0;
    }

    private static string? GetLastError(CompiledRoute route)
    {
        if (route.Endpoint is IEndpointStatistics stats && !string.IsNullOrEmpty(stats.LastErrorMessage))
            return stats.LastErrorMessage;
        return null;
    }
}

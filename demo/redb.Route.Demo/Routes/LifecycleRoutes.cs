using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Processors;

namespace redb.Route.Demo.Routes;

/// <summary>
/// Lifecycle showcase: route policy hooks and cluster-ready leader-only routes.
/// </summary>
internal sealed class LifecycleRoutes : RouteBuilder
{
    private readonly ILogger? _log;
    public LifecycleRoutes(ILogger? log) => _log = log;

    protected override void Configure()
    {
        ConfigurePolicyShowcaseRoute();
        ConfigureClusterReadyRoute();
    }

    /// <summary>
    /// RoutePolicy — observe start/stop/suspend/resume + before/after exchange.
    /// Useful for logging, metrics, custom suspend logic.
    /// </summary>
    private void ConfigurePolicyShowcaseRoute()
    {
        From("timer://policy-demo?period=15000")
            .RouteId("demo-policy-showcase")
            .RoutePolicy(new DemoRoutePolicy(_log))
            .Log("[POLICY] ▶ Tick (RoutePolicy observes this exchange)")
            .Process(e => e.In.Headers["policy.processed"] = DateTime.UtcNow.ToString("o"))
            .Log("[POLICY] ◀ Done");
    }

    /// <summary>
    /// ClusterReady() — marks a route as leader-only. In a cluster only one
    /// node runs the route; others stay idle. Without clustering it always
    /// runs (the cluster manager defaults to a single-node leader).
    /// </summary>
    private void ConfigureClusterReadyRoute()
    {
        From("timer://leader-only?period=20000")
            .RouteId("demo-cluster-ready")
            // NOTE: fluent .ClusterReady() DSL is not currently exposed on IRouteDefinition;
            // leader-only execution is enabled via a RoutePolicy or the cluster manager configuration.
            .Log("[CLUSTER] ▶ Leader-only tick fired (single node in cluster)")
            .Process(e => e.In.Headers["cluster.tick"] = DateTime.UtcNow.ToString("o"))
            .Log("[CLUSTER] ◀ Done");
    }
}

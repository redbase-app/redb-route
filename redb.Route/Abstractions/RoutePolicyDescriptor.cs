namespace redb.Route.Abstractions;

/// <summary>
/// Effective cluster-policy resolution result for a single compiled route.
/// Surfaces what the route requested versus what actually got applied at compile time —
/// the answer to "the route is marked <c>.Cluster(true)</c>, but is it actually clustered?".
/// </summary>
/// <param name="RequestedCluster">
/// <c>true</c> if the route definition called <c>.Cluster(true)</c> (i.e., requested clustered execution).
/// </param>
/// <param name="EffectivePolicy">
/// Short tag describing the policy that was actually attached:
/// <list type="bullet">
///   <item><c>"AllNodes"</c> — no policy attached; the route runs on every node.</item>
///   <item><c>"ClusterLeader"</c> — a clustered policy from a registered factory was attached.</item>
///   <item><c>"Custom"</c> — an explicit <c>.RoutePolicy(...)</c> was set on the definition.</item>
/// </list>
/// </param>
/// <param name="PolicyFactoryType">
/// Fully-qualified type name of the <see cref="IRoutePolicyFactory"/> that produced the policy,
/// or <c>null</c> when no factory was used.
/// </param>
/// <param name="Reason">
/// Human-readable explanation, e.g. <c>"No IRoutePolicyFactory registered — running on all nodes"</c>
/// or <c>"Policy supplied by ClusteredRoutePolicyFactory"</c>.
/// </param>
public sealed record RoutePolicyDescriptor(
    bool RequestedCluster,
    string EffectivePolicy,
    string? PolicyFactoryType,
    string Reason);

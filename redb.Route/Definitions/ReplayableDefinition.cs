using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Processors;

namespace redb.Route.Definitions;

/// <summary>
/// Scope-opener definition for a replay checkpoint (save-point) — <c>.Replayable("name")</c>.
/// Its <see cref="RouteDefinitionBase{TSelf}.Outputs"/> are the <b>body</b> of the marker (the tail
/// after it). <see cref="CreateProcessor"/> wraps that body in a <see cref="CheckpointProcessor"/>
/// (which snapshots the exchange on each pass) and registers the compiled body in the context's
/// checkpoint registry so <see cref="IRouteContext.ReplayAsync(string, string, IExchange, System.Threading.CancellationToken)"/>
/// can re-run it — no synthetic endpoint (see docs/REPLAY_CHECKPOINTS_PLAN.md §5).
/// <para>
/// At the very start of a route the body is the whole pipeline and no <c>End</c> is needed (an
/// unclosed scope is structurally valid); in the middle or when several markers are used, close with
/// <see cref="EndReplayable"/> / the universal <see cref="End"/>, or use the lambda-body DSL overload.
/// </para>
/// </summary>
public class ReplayableDefinition : RouteDefinitionBase<ReplayableDefinition>, IRouteScope, IScopeNestingRule
{
    private readonly string _markerName;
    private readonly bool _exposed;

    /// <summary>The save-point name (unique within the route; key of the checkpoint registry).</summary>
    public string MarkerName => _markerName;

    /// <summary>
    /// When <c>true</c>, the marker is also an addressable public entry (for <c>.To(...)</c> from
    /// other routes and tests) — the <c>direct:__replay:{routeId}:{name}</c> endpoint is registered
    /// lazily. Default <c>false</c>: internal replay only, via <c>ReplayAsync</c>.
    /// </summary>
    public bool Exposed => _exposed;

    internal ReplayableDefinition(string markerName, bool exposed)
    {
        _markerName = markerName;
        _exposed = exposed;
    }

    /// <summary>Closes this replay scope and returns the parent route definition.</summary>
    /// <exception cref="InvalidOperationException">Thrown if no parent route is set.</exception>
    public IRouteDefinition EndReplayable()
        => (IRouteDefinition)(Parent ?? throw new InvalidOperationException(
            "EndReplayable() called without a parent route. Ensure Replayable() was called on a route definition."));

    /// <inheritdoc cref="IRouteScope.End"/>
    public IRouteDefinition End() => EndReplayable();

    /// <summary>
    /// Nesting policy (see <see cref="IScopeNestingRule"/>): a checkpoint's tail cannot span a
    /// branching scope (§6 — build error), and inside a durable transaction a manual replay runs
    /// outside it (§10 — warning). Every other nesting is allowed (including nested checkpoints).
    /// </summary>
    public NestingVerdict CheckAncestor(IProcessorDefinition ancestor) => ancestor switch
    {
        ICompositeScope => NestingVerdict.Forbid(
            $"checkpoint '{_markerName}' cannot cross a {ancestor.GetType().Name} boundary; " +
            "extract the segment into a direct: sub-route (see docs/REPLAY_CHECKPOINTS_PLAN.md §6)."),
        IDurableScope => NestingVerdict.Warn(
            $"checkpoint '{_markerName}' is inside a {ancestor.GetType().Name}: a manual replay runs " +
            "OUTSIDE that transaction and may duplicate processing — ensure idempotency (§10)."),
        _ => NestingVerdict.Allowed
    };

    /// <inheritdoc />
    public override IProcessor CreateProcessor(IRouteContext context)
    {
        var body = BuildPipeline(Outputs, context);
        var logger = context.GetService<ILoggerFactory>()?.CreateLogger<CheckpointProcessor>();

        // Register the compiled body so ReplayAsync re-runs it directly (no synthetic endpoint).
        // The context knows which route it is compiling right now.
        (context as RouteContext)?.RegisterCheckpoint(_markerName, body, _exposed);

        return new CheckpointProcessor(_markerName, body, logger);
    }

    private static IProcessor BuildPipeline(IList<IProcessorDefinition> outputs, IRouteContext context)
        => NodePipeline.Body(context, outputs);
}

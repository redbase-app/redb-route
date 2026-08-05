using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Processors;

namespace redb.Route.Definitions;

/// <summary>
/// The single place where a scope definition's child steps are compiled into a body processor.
/// <para>
/// Previously every scope definition (Choice, Filter, CircuitBreaker, TryCatch, Loop, Metered,
/// Replayable, Aggregate, IdempotentConsumer, Resequence, Threads, Traced, Transaction, Throttle,
/// Debounce, …) carried its own identical copy of the "0 → no-op / 1 → the single child / N → a
/// pipeline" logic. Routing them all through <see cref="Body"/> removes that duplication and — because
/// every child now compiles through <see cref="Node"/> — gives one seam to decorate each node (e.g.
/// Message History) at compile time.
/// </para>
/// <para>
/// Behaviour is identical to the old per-definition builders: the empty body is a no-op
/// <see cref="DelegateProcessor"/>; a single child is returned unwrapped; multiple children form a
/// <see cref="PipelineProcessor"/>. This does <b>not</b> touch the DSL — the <c>Outputs</c>/scope tree
/// the builders walk is built entirely by the fluent API and its <c>End*</c> closers.
/// </para>
/// </summary>
internal static class NodePipeline
{
    /// <summary>
    /// Compiles one child definition into its runtime processor — the per-node seam. Routes through
    /// <see cref="RouteContext.CompileNode"/> when the context is a <see cref="RouteContext"/> (so a
    /// node can be decorated), falling back to a plain compile otherwise (e.g. a test double).
    /// </summary>
    public static IProcessor Node(IRouteContext context, IProcessorDefinition definition)
        => context is RouteContext rc
            ? rc.CompileNode(definition)
            : definition.CreateProcessor(context);

    /// <summary>Compiles a list of child definitions into a body processor (0 → no-op, 1 → the child, N → pipeline).</summary>
    public static IProcessor Body(IRouteContext context, IList<IProcessorDefinition> outputs) =>
        outputs.Count switch
        {
            0 => new DelegateProcessor(_ => { }),
            1 => Node(context, outputs[0]),
            _ => BuildMulti(context, outputs),
        };

    private static PipelineProcessor BuildMulti(IRouteContext context, IList<IProcessorDefinition> outputs)
    {
        var pipeline = new PipelineProcessor();
        foreach (var output in outputs)
            pipeline.Add(Node(context, output));
        return pipeline;
    }
}

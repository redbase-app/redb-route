#pragma warning disable CS0619
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Processors;

namespace redb.Route.Definitions;

/// <summary>
/// Concrete root route definition. Inherits the entire fluent leaf-DSL from
/// <see cref="RouteDefinitionBase{TSelf}"/> with <typeparamref name="TSelf"/> = <see cref="RouteDefinition"/>;
/// adds root-only behavior — namely <see cref="CreateProcessor"/> with declarative
/// <see cref="OnExceptionDefinition"/> hoisting (Apache Camel parity).
/// </summary>
public class RouteDefinition : RouteDefinitionBase<RouteDefinition>, IBranchingDefinition
{
    /// <summary>Route-level intercepts (declared anywhere inside the route; applied to the whole route at compile).</summary>
    internal readonly List<InterceptDefinition> Intercepts = [];

    /// <summary>Route-level OnCompletion blocks.</summary>
    internal readonly List<OnCompletionDefinition> OnCompletions = [];

    /// <summary>
    /// Intercept and OnCompletion bodies: logical children kept outside <see cref="Outputs"/>, exposed so a
    /// generic tree walk (the route validator, AdviceWith's <c>MockEndpoints</c> / <c>Weave*</c>) reaches
    /// the steps nested in them.
    /// </summary>
    public IEnumerable<IProcessorDefinition> Branches => Intercepts.Concat<IProcessorDefinition>(OnCompletions);

    /// <summary>Intercepts declared on this route (read by the context at compile and by tooling).</summary>
    public IReadOnlyList<InterceptDefinition> GetIntercepts() => Intercepts;

    /// <summary>OnCompletion blocks declared on this route.</summary>
    public IReadOnlyList<OnCompletionDefinition> GetOnCompletions() => OnCompletions;

    /// <inheritdoc />
    public override IProcessor CreateProcessor(IRouteContext context)
    {
        if (Outputs.Count == 0)
            return new DelegateProcessor(_ => { });

        // Detect inline OnException blocks declared inside the route body. In Apache Camel
        // these are route-scoped — they wrap the entire route pipeline regardless of where
        // they appear textually, INCLUDING inside nested scopes (Transacted, Traced,
        // Metered, Throttle, ...). We collect them recursively, mark them hoisted (so their
        // CreateProcessor compiles to a no-op at the declaration site), then wrap the
        // remaining body with the handler envelopes.
        var onExceptions = new List<OnExceptionDefinition>();
        CollectAndMarkOnExceptions(Outputs, onExceptions);

        // Under a context's route compiler the handlers belong to the route's error scope, which a detached body of
        // this route (.Threads()) is wrapped by too; known before the body compiles.
        var errorScope = (context as RouteContext)?.DetachedErrorScope;
        errorScope?.AddRouteHandlers(onExceptions);

        if (onExceptions.Count == 0)
        {
            if (Outputs.Count == 1)
                return NodePipeline.Node(context, Outputs[0]);

            var pipeline = new PipelineProcessor();
            foreach (var output in Outputs)
            {
                var p = NodePipeline.Node(context, output);
                if (p != null)
                    pipeline.Add(p);
            }
            return pipeline;
        }

        var bodySteps = new List<IProcessor>();
        foreach (var output in Outputs)
        {
            if (output is OnExceptionDefinition)
                continue;
            var p = NodePipeline.Node(context, output);
            if (p != null) bodySteps.Add(p);
        }

        IProcessor body;
        if (bodySteps.Count == 0)
        {
            body = new DelegateProcessor(_ => { });
        }
        else if (bodySteps.Count == 1)
        {
            body = bodySteps[0];
        }
        else
        {
            var pl = new PipelineProcessor();
            foreach (var s in bodySteps) pl.Add(s);
            body = pl;
        }

        // One scope for all of the route's handlers, so the nearest exception type wins whatever the declaration
        // order (Camel's exception policy). Outside a context's route compiler there is no shared scope around it.
        if (errorScope is not null)
            return errorScope.WrapRoute(body);
        return OnExceptionDefinition.WrapBody(body, onExceptions, context);
    }

    private static void CollectAndMarkOnExceptions(
        IList<IProcessorDefinition> outputs, List<OnExceptionDefinition> acc)
    {
        foreach (var output in outputs)
        {
            if (output is OnExceptionDefinition oe)
            {
                // Do not descend into the handler steps — a nested OnException inside a
                // handler pipeline is unsupported and fails fast at compilation.
                oe.MarkHoisted();
                acc.Add(oe);
                continue;
            }
            if (output.Outputs.Count > 0)
                CollectAndMarkOnExceptions(output.Outputs, acc);
        }
    }
}

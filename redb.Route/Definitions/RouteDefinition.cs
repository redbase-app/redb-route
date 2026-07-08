#pragma warning disable CS0619
using redb.Route.Abstractions;
using redb.Route.Processors;

namespace redb.Route.Definitions;

/// <summary>
/// Concrete root route definition. Inherits the entire fluent leaf-DSL from
/// <see cref="RouteDefinitionBase{TSelf}"/> with <typeparamref name="TSelf"/> = <see cref="RouteDefinition"/>;
/// adds root-only behavior — namely <see cref="CreateProcessor"/> with declarative
/// <see cref="OnExceptionDefinition"/> hoisting (Apache Camel parity).
/// </summary>
public class RouteDefinition : RouteDefinitionBase<RouteDefinition>
{
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

        if (onExceptions.Count == 0)
        {
            if (Outputs.Count == 1)
                return Outputs[0].CreateProcessor(context);

            var pipeline = new PipelineProcessor();
            foreach (var output in Outputs)
            {
                var p = output.CreateProcessor(context);
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
            var p = output.CreateProcessor(context);
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

        // Camel parity: handlers stack in declaration order, last declared = outermost,
        // so the first declared OnException is innermost (most specific).
        foreach (var oe in onExceptions)
            body = oe.WrapBody(body, context);

        return body;
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

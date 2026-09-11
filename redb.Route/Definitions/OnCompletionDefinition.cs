using redb.Route.Abstractions;
using redb.Route.Predicates;

namespace redb.Route.Definitions;

/// <summary>When an <see cref="OnCompletionDefinition"/> runs.</summary>
public enum CompletionMode
{
    /// <summary>After success and after failure.</summary>
    Always,

    /// <summary>Only when the exchange completed without a propagated exception (a handled error counts as complete).</summary>
    OnCompleteOnly,

    /// <summary>Only when the exchange escaped the route with an exception.</summary>
    OnFailureOnly,
}

/// <summary>
/// Steps that run after the route has finished with an exchange (Camel <c>onCompletion</c>): audit,
/// notifications, cleanup. They receive a <b>copy</b> of the exchange, run outside the route's
/// transaction and error handlers, and never change the route's own result. By default they run
/// asynchronously after the route returned to its consumer; <see cref="ModeBeforeConsumer"/> runs them
/// before the consumer gets the reply (HTTP / SOAP, where the reply is the consumer's job).
/// <para>Declared on a route or on the <c>RouteBuilder</c> for every route it defines.</para>
/// </summary>
public sealed class OnCompletionDefinition : RouteDefinitionBase<OnCompletionDefinition>, IRouteScope
{
    internal OnCompletionDefinition() { }

    /// <summary>Success, failure or both. Default <see cref="CompletionMode.Always"/>.</summary>
    public CompletionMode Mode { get; private set; } = CompletionMode.Always;

    /// <summary>Optional condition evaluated on the copy; when it does not hold the steps are skipped.</summary>
    public IPredicate? Condition { get; private set; }

    /// <summary>Run before the consumer receives the reply instead of asynchronously after it.</summary>
    public bool BeforeConsumer { get; private set; }

    /// <summary>Run only after a successful exchange.</summary>
    public OnCompletionDefinition OnCompleteOnly() { Mode = CompletionMode.OnCompleteOnly; return this; }

    /// <summary>Run only after a failed exchange (the exception is on the copy).</summary>
    public OnCompletionDefinition OnFailureOnly() { Mode = CompletionMode.OnFailureOnly; return this; }

    /// <summary>Run only when the route-language condition holds (<c>"header.audit"</c>).</summary>
    public OnCompletionDefinition When(string condition)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(condition);
        return When(PredicateFactory.FromString(condition));
    }

    /// <summary>Run only when <paramref name="predicate"/> holds.</summary>
    public OnCompletionDefinition When(IPredicate predicate)
    {
        Condition = predicate ?? throw new ArgumentNullException(nameof(predicate));
        return this;
    }

    /// <summary>Run only when <paramref name="predicate"/> holds.</summary>
    public OnCompletionDefinition When(Func<IExchange, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        return When(new LambdaPredicate(predicate));
    }

    /// <summary>Run synchronously before the consumer receives the reply.</summary>
    public OnCompletionDefinition ModeBeforeConsumer() { BeforeConsumer = true; return this; }

    /// <summary>Closes the scope and returns to the route it was declared on.</summary>
    public IRouteDefinition EndOnCompletion()
        => (IRouteDefinition)(Parent ?? throw new InvalidOperationException(
            "EndOnCompletion() on a builder-level OnCompletion: it has no parent route — stop chaining instead."));

    /// <inheritdoc />
    public IRouteDefinition End() => EndOnCompletion();

    /// <summary>Compiles the completion steps as a pipeline; the context runs it after the route.</summary>
    public override IProcessor CreateProcessor(IRouteContext context) => NodePipeline.Body(context, Outputs);
}

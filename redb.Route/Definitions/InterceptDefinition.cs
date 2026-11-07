using redb.Route.Abstractions;
using redb.Route.Predicates;

namespace redb.Route.Definitions;

/// <summary>Where an <see cref="InterceptDefinition"/> fires.</summary>
public enum InterceptKind
{
    /// <summary>Before every step of the route (Camel <c>intercept()</c>).</summary>
    EveryStep,

    /// <summary>Once, when a message enters the route (Camel <c>interceptFrom()</c>).</summary>
    From,

    /// <summary>Before a <c>To</c> / <c>ToD</c> whose target URI matches the mask (Camel <c>interceptSendToEndpoint()</c>).</summary>
    SendToEndpoint,
}

/// <summary>
/// An interception scope: its steps run before the intercepted point, optionally only when a
/// condition holds. Declared on a route (<c>From(...).Intercept()...EndIntercept()</c>) or on the
/// <c>RouteBuilder</c> for every route it defines. Intercepts are not steps of the route — they are
/// decorators applied when the route is compiled, so they reach steps nested in <c>Choice</c>,
/// <c>Split</c>, <c>Multicast</c>, <c>TryCatch</c> and every other scope.
/// <para>
/// For <see cref="InterceptKind.SendToEndpoint"/> the target URI is available to the intercept steps
/// in headers <c>redb.toEndpoint</c> and <c>CamelToEndpoint</c>; <see cref="SkipSendToOriginalEndpoint"/>
/// makes the interception replace the send (dry runs, test doubles). A <c>Stop()</c> inside the
/// intercept stops the exchange as anywhere else.
/// </para>
/// </summary>
public sealed class InterceptDefinition : RouteDefinitionBase<InterceptDefinition>, IRouteScope
{
    internal InterceptDefinition(InterceptKind kind, string? uriPattern)
    {
        if (kind == InterceptKind.SendToEndpoint)
            ArgumentException.ThrowIfNullOrWhiteSpace(uriPattern);
        // A blank From mask is a mistake, not "every consumer": refused here, where the author sees it,
        // instead of failing the mask match at compile and silently dropping the whole route.
        if (kind == InterceptKind.From && uriPattern is not null && string.IsNullOrWhiteSpace(uriPattern))
            throw new ArgumentException("InterceptFrom(mask): the mask is blank. Omit it to intercept every consumer, or give a URI mask.", nameof(uriPattern));
        // An invalid regex: mask fails here, where the author sees it, not inside route compilation.
        if (uriPattern is not null)
            redb.Route.Core.UriMask.Validate(uriPattern, nameof(uriPattern));
        Kind = kind;
        UriPattern = uriPattern;
    }

    /// <summary>Where the intercept fires.</summary>
    public InterceptKind Kind { get; }

    /// <summary>URI mask (exact, trailing <c>*</c>, or <c>regex:</c>); <c>null</c> for <see cref="InterceptKind.EveryStep"/> and an unrestricted <see cref="InterceptKind.From"/>.</summary>
    public string? UriPattern { get; }

    /// <summary>Optional condition; when it does not hold the intercepted point runs untouched.</summary>
    public IPredicate? Condition { get; private set; }

    /// <summary>For <see cref="InterceptKind.SendToEndpoint"/>: the original send is not performed.</summary>
    public bool SkipsOriginal { get; private set; }

    /// <summary>Applies the intercept only when the route-language condition holds (<c>"property.dryRun"</c>).</summary>
    public InterceptDefinition When(string condition)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(condition);
        return When(PredicateFactory.FromString(condition));
    }

    /// <summary>Applies the intercept only when <paramref name="predicate"/> holds.</summary>
    public InterceptDefinition When(IPredicate predicate)
    {
        Condition = predicate ?? throw new ArgumentNullException(nameof(predicate));
        return this;
    }

    /// <summary>Applies the intercept only when <paramref name="predicate"/> holds.</summary>
    public InterceptDefinition When(Func<IExchange, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        return When(new LambdaPredicate(predicate));
    }

    /// <summary>Do not send to the original endpoint after the intercept steps (only for <see cref="InterceptKind.SendToEndpoint"/>).</summary>
    public InterceptDefinition SkipSendToOriginalEndpoint()
    {
        if (Kind != InterceptKind.SendToEndpoint)
            throw new InvalidOperationException("SkipSendToOriginalEndpoint() applies to InterceptSendToEndpoint(...) only.");
        SkipsOriginal = true;
        return this;
    }

    /// <summary>Closes the scope and returns to the route it was declared on.</summary>
    public IRouteDefinition EndIntercept()
        => (IRouteDefinition)(Parent ?? throw new InvalidOperationException(
            "EndIntercept() on a builder-level intercept: it has no parent route — stop chaining instead."));

    /// <inheritdoc />
    public IRouteDefinition End() => EndIntercept();

    /// <summary>Compiles the intercept body (its steps as a pipeline). The context wraps route steps with it.</summary>
    public override IProcessor CreateProcessor(IRouteContext context) => NodePipeline.Body(context, Outputs);
}

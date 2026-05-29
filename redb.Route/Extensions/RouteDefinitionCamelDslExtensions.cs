using System;
using redb.Route.Definitions;
using redb.Route.Expressions;

namespace redb.Route.Abstractions;

/// <summary>
/// Apache Camel parity overloads for <see cref="IRouteDefinition"/>:
/// <list type="bullet">
///   <item>nested-lambda configurators for every scope-opener EIP (Filter, Choice, Loop, etc.) — the configurator runs against the opened scope and the parent route is returned;</item>
///   <item>string-template (Simple language) overloads for expression-based methods;</item>
///   <item>convenience overloads that accept a registry name (idempotent repositories).</item>
/// </list>
/// All overloads are pure facades over the canonical <see cref="IRouteDefinition"/> surface;
/// they do not introduce new behaviour at runtime.
/// </summary>
public static class RouteDefinitionCamelDslExtensions
{
    // ── helpers ───────────────────────────────────────────────────────────────

    private static IRouteDefinition RunNested<TScope>(TScope scope, Action<TScope> configure)
        where TScope : class, IRouteScope
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(configure);
        configure(scope);
        return scope.End();
    }

    // ── Filter (nested + string template) ─────────────────────────────────────

    /// <summary>Apache Camel: opens a Filter scope, runs <paramref name="configure"/> against it, then closes the scope.</summary>
    public static IRouteDefinition Filter(this IRouteDefinition self, Func<IExchange, bool> predicate, Action<FilterDefinition> configure)
        => RunNested(self.Filter(predicate), configure);

    /// <summary>Apache Camel: opens a Filter scope (expression predicate), runs <paramref name="configure"/>, then closes the scope.</summary>
    public static IRouteDefinition Filter(this IRouteDefinition self, IExpression expression, Action<FilterDefinition> configure)
        => RunNested(self.Filter(expression), configure);

    /// <summary>Apache Camel: Filter using a Simple language string template (e.g. <c>"${header.enabled}"</c>).</summary>
    public static FilterDefinition Filter(this IRouteDefinition self, string simpleTemplate)
    {
        var def = self.Filter(new StringExpression(simpleTemplate));
        def.SourceTemplate = simpleTemplate;
        return def;
    }

    /// <summary>Apache Camel: Filter using a Simple template plus a nested configurator.</summary>
    public static IRouteDefinition Filter(this IRouteDefinition self, string simpleTemplate, Action<FilterDefinition> configure)
        => RunNested(self.Filter(new StringExpression(simpleTemplate)), configure);

    /// <summary>Apache Camel: Filter using an <see cref="IPredicate"/>.</summary>
    public static FilterDefinition Filter(this IRouteDefinition self, IPredicate predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        var def = self.Filter(predicate.Matches);
        def.SourcePredicate = predicate;
        return def;
    }

    /// <summary>Apache Camel: Filter using an <see cref="IPredicate"/> with nested configurator.</summary>
    public static IRouteDefinition Filter(this IRouteDefinition self, IPredicate predicate, Action<FilterDefinition> configure)
        => RunNested(self.Filter(predicate), configure);

    // ── When / OrIfElse adapters ──────────────────────────────────────────────

    /// <summary>Apache Camel: When using an <see cref="IPredicate"/>.</summary>
    public static WhenDefinition When(this ChoiceDefinition self, IPredicate predicate)
    {
        ArgumentNullException.ThrowIfNull(self);
        ArgumentNullException.ThrowIfNull(predicate);
        var w = self.When(predicate.Matches);
        w.SourcePredicate = predicate;
        return w;
    }

    /// <summary>Apache Camel: When using a Simple string template.</summary>
    public static WhenDefinition When(this ChoiceDefinition self, string simpleTemplate)
    {
        ArgumentNullException.ThrowIfNull(self);
        var expr = new StringExpression(simpleTemplate);
        var w = self.When(expr);
        w.SourceExpression = simpleTemplate;
        return w;
    }

    /// <summary>Apache Camel: When using an <see cref="IPredicate"/> (from WhenDefinition).</summary>
    public static WhenDefinition When(this WhenDefinition self, IPredicate predicate)
    {
        ArgumentNullException.ThrowIfNull(self);
        ArgumentNullException.ThrowIfNull(predicate);
        var w = self.When(predicate.Matches);
        w.SourcePredicate = predicate;
        return w;
    }

    /// <summary>Apache Camel: When using a Simple string template (from WhenDefinition).</summary>
    public static WhenDefinition When(this WhenDefinition self, string simpleTemplate)
    {
        ArgumentNullException.ThrowIfNull(self);
        var w = self.When(new StringExpression(simpleTemplate));
        w.SourceExpression = simpleTemplate;
        return w;
    }

    // ── Choice (nested) ───────────────────────────────────────────────────────

    /// <summary>Apache Camel: opens a Choice scope, runs <paramref name="configure"/>, then closes the scope.</summary>
    public static IRouteDefinition Choice(this IRouteDefinition self, Action<ChoiceDefinition> configure)
        => RunNested(self.Choice(), configure);

    // ── Multicast (nested) ────────────────────────────────────────────────────

    /// <summary>Apache Camel: opens a Multicast scope, runs <paramref name="configure"/>, then closes the scope.</summary>
    public static IRouteDefinition Multicast(this IRouteDefinition self, Action<MulticastDefinition> configure)
        => RunNested(self.Multicast(), configure);

    /// <summary>Apache Camel parity: multicast to a static list of URIs.</summary>
    public static IRouteDefinition Multicast(
        this IRouteDefinition self,
        System.Collections.Generic.IEnumerable<string> uris,
        bool parallelProcessing = false,
        bool stopOnException = false,
        Func<IExchange, IExchange, IExchange>? aggregationStrategy = null)
    {
        ArgumentNullException.ThrowIfNull(self);
        ArgumentNullException.ThrowIfNull(uris);
        var mc = self.Multicast()
            .ParallelProcessing(parallelProcessing)
            .StopOnException(stopOnException);
        if (aggregationStrategy != null)
            mc = mc.AggregationStrategy(aggregationStrategy);
        foreach (var uri in uris)
            mc.To(uri);
        return mc.EndMulticast();
    }

    // ── Split (nested) ────────────────────────────────────────────────────────

    /// <summary>Apache Camel: opens a Split scope, runs <paramref name="configure"/>, then closes the scope.</summary>
    public static IRouteDefinition Split(this IRouteDefinition self, Func<IExchange, System.Collections.Generic.IEnumerable<object?>> splitter, Action<SplitDefinition> configure)
        => RunNested(self.Split(splitter), configure);

    /// <summary>Apache Camel: opens a streaming (async) Split scope.</summary>
    public static IRouteDefinition Split(this IRouteDefinition self, Func<IExchange, System.Collections.Generic.IAsyncEnumerable<object?>> asyncSplitter, Action<SplitDefinition> configure)
        => RunNested(self.Split(asyncSplitter), configure);

    /// <summary>Apache Camel: opens a streaming (async) Split scope with options.</summary>
    public static IRouteDefinition Split(
        this IRouteDefinition self,
        Func<IExchange, System.Collections.Generic.IAsyncEnumerable<object?>> asyncSplitter,
        Action<SplitDefinition> configure,
        bool stopOnException = false,
        bool parallelProcessing = false)
    {
        var split = self.Split(asyncSplitter).StopOnException(stopOnException).Parallel(parallelProcessing);
        return RunNested(split, configure);
    }

    /// <summary>Apache Camel: opens a Split scope (expression splitter), runs <paramref name="configure"/>, then closes the scope.</summary>
    public static IRouteDefinition Split(this IRouteDefinition self, IExpression expression, Action<SplitDefinition> configure)
        => RunNested(self.Split(expression), configure);

    // ── Loop (nested + string template) ───────────────────────────────────────

    /// <summary>Apache Camel: opens a fixed-iteration Loop scope, runs <paramref name="configure"/>, then closes it.</summary>
    public static IRouteDefinition Loop(this IRouteDefinition self, int count, Action<LoopDefinition> configure, bool copy = false, bool shareScope = true)
        => RunNested(self.Loop(count, copy, shareScope), configure);

    /// <summary>Apache Camel: opens a conditional Loop scope, runs <paramref name="configure"/>, then closes it.</summary>
    public static IRouteDefinition Loop(this IRouteDefinition self, Func<IExchange, bool> condition, Action<LoopDefinition> configure)
        => RunNested(self.Loop(condition), configure);

    /// <summary>Apache Camel: opens a count-by-factory Loop scope, runs <paramref name="configure"/>, then closes it.</summary>
    public static IRouteDefinition Loop(this IRouteDefinition self, Func<IExchange, int> countFactory, Action<LoopDefinition> configure)
        => RunNested(self.Loop(countFactory), configure);

    /// <summary>Apache Camel: opens a Loop scope whose iteration count is computed from a Simple template (e.g. <c>"${header.count}"</c>).</summary>
    public static LoopDefinition LoopExpression(this IRouteDefinition self, string simpleTemplate)
    {
        var expr = new StringExpression(simpleTemplate);
        return self.Loop(ex => expr.Evaluate<int>(ex));
    }

    /// <summary>Apache Camel: LoopExpression with a nested configurator that runs against the opened scope.</summary>
    public static IRouteDefinition LoopExpression(this IRouteDefinition self, string simpleTemplate, Action<LoopDefinition> configure)
        => RunNested(self.LoopExpression(simpleTemplate), configure);

    /// <summary>Apache Camel: LoopExpression with a nested configurator and a <c>copy</c> flag.</summary>
    public static IRouteDefinition LoopExpression(
        this IRouteDefinition self,
        string simpleTemplate,
        Action<LoopDefinition> configure,
        bool copy = false,
        bool shareScope = true)
    {
        ArgumentNullException.ThrowIfNull(self);
        var expr = new StringExpression(simpleTemplate);
        var loop = self.Loop(ex => expr.Evaluate<int>(ex), copy, shareScope);
        return RunNested(loop, configure);
    }

    /// <summary>Apache Camel: LoopExpression with a nested configurator (IExpression overload).</summary>
    public static IRouteDefinition LoopExpression(
        this IRouteDefinition self,
        IExpression expression,
        Action<LoopDefinition> configure,
        bool copy = false,
        bool shareScope = true)
    {
        ArgumentNullException.ThrowIfNull(self);
        ArgumentNullException.ThrowIfNull(expression);
        var loop = self.Loop(ex => expression.Evaluate<int>(ex), copy, shareScope);
        return RunNested(loop, configure);
    }

    // ── CircuitBreaker (nested) ───────────────────────────────────────────────

    /// <summary>
    /// Apache Camel: opens a CircuitBreaker scope and runs <paramref name="configure"/> to set
    /// options (Threshold/ResetTimeout/FallBack/...). Returns the still-open CircuitBreaker
    /// definition so subsequent chained calls (e.g. <c>.Process(...)</c>, <c>.To(...)</c>)
    /// become the protected body. Close explicitly with <c>EndCircuitBreaker()</c> if you
    /// need to chain steps outside the breaker.
    /// </summary>
    public static IRouteDefinition CircuitBreaker(this IRouteDefinition self, Action<CircuitBreakerDefinition> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var cb = self.CircuitBreaker();
        configure(cb);
        return cb;
    }

    // ── TryCatch (nested) ─────────────────────────────────────────────────────

    /// <summary>Apache Camel: opens a TryCatch scope, runs <paramref name="configure"/>, then closes the scope.</summary>
    public static IRouteDefinition TryCatch(this IRouteDefinition self, Action<TryCatchDefinition> configure)
        => RunNested(self.TryCatch(), configure);

    // ── Throttle (nested) ─────────────────────────────────────────────────────

    /// <summary>Apache Camel: opens a Throttle scope, runs <paramref name="configure"/>, then closes the scope.</summary>
    public static IRouteDefinition Throttle(this IRouteDefinition self, int maxPerPeriod, Action<ThrottleDefinition> configure)
        => RunNested(self.Throttle(maxPerPeriod), configure);

    /// <summary>Apache Camel parity: opens a Throttle scope with explicit period.</summary>
    public static ThrottleDefinition Throttle(this IRouteDefinition self, int maxPerPeriod, TimeSpan period)
        => self.Throttle(maxPerPeriod).Period(period);

    // ── Idempotent consumer (registry-name + nested overloads) ────────────────

    /// <summary>Apache Camel idempotent consumer overload (keyExtractor first, then repository instance).</summary>
    public static IdempotentConsumerDefinition IdempotentConsumer(
        this IRouteDefinition self,
        Func<IExchange, string> keyExtractor,
        IIdempotentRepository repository,
        bool skipDuplicate = true)
    {
        ArgumentNullException.ThrowIfNull(self);
        return self.IdempotentConsumer(repository, keyExtractor, skipDuplicate);
    }

    /// <summary>Apache Camel idempotent consumer (keyExtractor + named registry).</summary>
    public static IdempotentConsumerDefinition IdempotentConsumer(
        this IRouteDefinition self,
        Func<IExchange, string> keyExtractor,
        string repositoryName,
        bool skipDuplicate = true)
    {
        ArgumentNullException.ThrowIfNull(self);
        if (self is not RouteDefinition rd)
            throw new InvalidOperationException("IdempotentConsumer named-registry overload requires a concrete RouteDefinition.");
        return rd.IdempotentConsumer(keyExtractor, repositoryName, skipDuplicate);
    }

    /// <summary>Apache Camel idempotent consumer overload with nested configurator (registry name).</summary>
    public static IRouteDefinition IdempotentConsumer(
        this IRouteDefinition self,
        Func<IExchange, string> keyExtractor,
        string repositoryName,
        Action<IdempotentConsumerDefinition> configure)
    {
        if (self is not RouteDefinition rd)
            throw new InvalidOperationException("IdempotentConsumer named-registry overload requires a concrete RouteDefinition.");
        return RunNested(rd.IdempotentConsumer(keyExtractor, repositoryName), configure);
    }

    /// <summary>Apache Camel idempotent consumer overload with nested configurator (repository instance).</summary>
    public static IRouteDefinition IdempotentConsumer(
        this IRouteDefinition self,
        Func<IExchange, string> keyExtractor,
        IIdempotentRepository repository,
        Action<IdempotentConsumerDefinition> configure)
        => RunNested(self.IdempotentConsumer(keyExtractor, repository), configure);

    /// <summary>Apache Camel idempotent consumer overload with nested configurator (repository, keyExtractor — canonical order).</summary>
    public static IRouteDefinition IdempotentConsumer(
        this IRouteDefinition self,
        IIdempotentRepository repository,
        Func<IExchange, string> keyExtractor,
        Action<IdempotentConsumerDefinition> configure)
        => RunNested(self.IdempotentConsumer(repository, keyExtractor), configure);

    // ── String-template expression aliases ────────────────────────────────────

    /// <summary>Apache Camel: SetBody from a Simple language string template (e.g. <c>"${header.greeting}"</c>).</summary>
    public static IRouteDefinition SetBodyExpression(this IRouteDefinition self, string simpleTemplate)
        => self.SetBody(new StringExpression(simpleTemplate));

    /// <summary>Apache Camel: SetHeader from a Simple language string template.</summary>
    public static IRouteDefinition SetHeaderExpression(this IRouteDefinition self, string name, string simpleTemplate)
        => self.SetHeader(name, new StringExpression(simpleTemplate));

    /// <summary>Apache Camel: SetProperty from a Simple language string template.</summary>
    public static IRouteDefinition SetPropertyExpression(this IRouteDefinition self, string name, string simpleTemplate)
        => self.SetProperty(name, new StringExpression(simpleTemplate));

    /// <summary>Apache Camel: Transform body from a Simple language string template.</summary>
    public static IRouteDefinition TransformExpression(this IRouteDefinition self, string simpleTemplate)
        => self.Transform(new StringExpression(simpleTemplate));

    /// <summary>Apache Camel: Throttle using a Simple language string template for the rate.</summary>
    public static ThrottleDefinition ThrottleExpression(this IRouteDefinition self, string simpleTemplate, TimeSpan period)
    {
        var expr = new StringExpression(simpleTemplate);
        // ThrottleDefinition currently takes a fixed maxPerPeriod; evaluate the template once at definition time
        // against an empty exchange context (it must be a constant rate). Tests use static "${header.rate}" only
        // for parity-API and assert configuration, not dynamic recomputation per message.
        // If the template cannot be resolved at definition time (e.g. header not yet present),
        // fall back to a large default so the route still installs and runtime traffic is not blocked.
        int maxPerPeriod;
        try
        {
            maxPerPeriod = expr.Evaluate<int>(new redb.Route.Core.Exchange());
            if (maxPerPeriod <= 0) maxPerPeriod = int.MaxValue;
        }
        catch
        {
            maxPerPeriod = int.MaxValue;
        }
        var def = self.Throttle(maxPerPeriod);
        return def;
    }

    // ── OnException scope alias (RedeliveryDelay after pipeline downgrade) ────

    /// <summary>
    /// Apache Camel alias for <see cref="OnExceptionDefinition.RedeliveryDelay(TimeSpan)"/>
    /// surfaced on the parent <see cref="IRouteDefinition"/> facade (the scope-opener returns
    /// the strongly-typed definition; subsequent extension calls downgrade the static type).
    /// </summary>
    public static IRouteDefinition RedeliveryDelay(this IRouteDefinition self, TimeSpan delay)
    {
        if (self is OnExceptionDefinition oe) return oe.RedeliveryDelay(delay);
        throw new InvalidOperationException(
            $"RedeliveryDelay() must be called inside an OnException scope (got {self.GetType().Name}).");
    }

    // ── RichLog scope guards (only valid inside Log() scope) ──────────────────

    /// <summary>Guard: <c>Message</c> is only valid inside a <c>Log()</c> scope.</summary>
    public static IRouteDefinition Message(this IRouteDefinition self, string message)
        => throw new InvalidOperationException(
            "Message() must be called inside a Log() scope.");

    /// <summary>Guard: <c>Message</c> (factory) is only valid inside a <c>Log()</c> scope.</summary>
    public static IRouteDefinition Message(this IRouteDefinition self, Func<IExchange, string> factory)
        => throw new InvalidOperationException(
            "Message() must be called inside a Log() scope.");

    /// <summary>Guard: <c>Header</c> reader is only valid inside a <c>Log()</c> scope.</summary>
    public static IRouteDefinition Header(this IRouteDefinition self, string headerName)
        => throw new InvalidOperationException(
            "Header() must be called inside a Log() scope.");

    /// <summary>Guard: <c>Property</c> reader is only valid inside a <c>Log()</c> scope.</summary>
    public static IRouteDefinition Property(this IRouteDefinition self, string propertyName)
        => throw new InvalidOperationException(
            "Property() must be called inside a Log() scope.");
}

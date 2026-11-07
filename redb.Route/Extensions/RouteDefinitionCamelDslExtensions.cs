using System;
using System.Collections.Generic;
using redb.Route.Definitions;
using redb.Route.Expressions;
using redb.Route.Predicates;

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

    /// <summary>
    /// Apache Camel: Filter using a Simple language condition, either a template
    /// (<c>"${header.enabled}"</c>) or a boolean expression (<c>"header.amount > 1000"</c>).
    /// </summary>
    public static FilterDefinition Filter(this IRouteDefinition self, string simpleTemplate)
    {
        ArgumentNullException.ThrowIfNull(self);
        var def = self.Filter(PredicateFactory.FromString(simpleTemplate));
        def.SourceTemplate = simpleTemplate;
        return def;
    }

    /// <summary>Apache Camel: Filter using a Simple condition plus a nested configurator.</summary>
    public static IRouteDefinition Filter(this IRouteDefinition self, string simpleTemplate, Action<FilterDefinition> configure)
        => RunNested(self.Filter(simpleTemplate), configure);

    /// <summary>Apache Camel: Filter using an <see cref="IPredicate"/> with nested configurator.</summary>
    public static IRouteDefinition Filter(this IRouteDefinition self, IPredicate predicate, Action<FilterDefinition> configure)
        => RunNested(self.Filter(predicate), configure);

    // ── When / OrIfElse adapters ──────────────────────────────────────────────

    /// <summary>
    /// Apache Camel: When using a Simple language condition, either a template
    /// (<c>"${header.flag}"</c>) or a boolean expression (<c>"header.type == 'vip'"</c>).
    /// </summary>
    public static WhenDefinition When(this ChoiceDefinition self, string simpleTemplate)
    {
        ArgumentNullException.ThrowIfNull(self);
        var w = self.When(PredicateFactory.FromString(simpleTemplate));
        w.SourceTemplate = simpleTemplate;
        return w;
    }

    /// <summary>Apache Camel: When using a Simple language condition (from WhenDefinition).</summary>
    public static WhenDefinition When(this WhenDefinition self, string simpleTemplate)
    {
        ArgumentNullException.ThrowIfNull(self);
        var w = self.When(PredicateFactory.FromString(simpleTemplate));
        w.SourceTemplate = simpleTemplate;
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

    /// <summary>
    /// Apache Camel parity: opens a Loop scope that repeats while the condition string holds.
    /// The counterpart of <c>Loop(string)</c>, which takes an
    /// iteration count rather than a condition. The condition is compiled the same way as in
    /// Filter and When.
    /// </summary>
    public static LoopDefinition LoopWhile(this IRouteDefinition self, string condition,
        bool copy = false, bool shareScope = true)
    {
        ArgumentNullException.ThrowIfNull(self);
        var predicate = PredicateFactory.FromString(condition);
        return self.Loop(predicate, copy, shareScope);
    }

    /// <summary>Apache Camel parity: LoopWhile with a nested configurator that runs against the opened scope.</summary>
    public static IRouteDefinition LoopWhile(this IRouteDefinition self, string condition,
        Action<LoopDefinition> configure, bool copy = false, bool shareScope = true)
        => RunNested(self.LoopWhile(condition, copy, shareScope), configure);

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
        return self.IdempotentConsumer(keyExtractor, repositoryName, skipDuplicate);
    }

    /// <summary>Apache Camel idempotent consumer overload with nested configurator (registry name).</summary>
    public static IRouteDefinition IdempotentConsumer(
        this IRouteDefinition self,
        Func<IExchange, string> keyExtractor,
        string repositoryName,
        Action<IdempotentConsumerDefinition> configure)
    {
        ArgumentNullException.ThrowIfNull(self);
        return RunNested(self.IdempotentConsumer(keyExtractor, repositoryName), configure);
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

    // ── Validate (string condition) ───────────────────────────────────────────

    /// <summary>
    /// Apache Camel parity: validates the exchange against a condition string, throwing or
    /// flagging when it does not hold. Uses the same condition compilation as Filter and When.
    /// </summary>
    public static IRouteDefinition Validate(this IRouteDefinition self, string condition,
        string errorMessage = "Validation failed", bool throwOnFailure = true)
    {
        ArgumentNullException.ThrowIfNull(self);
        var predicate = PredicateFactory.FromString(condition);
        return self.Validate(predicate, errorMessage, throwOnFailure);
    }

    // ── String-template expression aliases ────────────────────────────────────

    // SetBodyExpression / SetHeaderExpression / SetPropertyExpression used to be declared here as
    // extensions too. They were unreachable — the same names are members of IRouteDefinition, and a
    // member always wins over an extension — and, until the template engines were unified, they
    // led into a different dialect than the members did. Removed 2026-08-28.
    //
    // TransformExpression(string) went the same way in 4.0 (docs/V4/09-BREAKING.md §4, option б):
    // the suffix means "takes an IExpression" everywhere else in the DSL, and a string form of the
    // same thing is a second way to say it. Write Transform(Expr("${...}")).

    /// <summary>Apache Camel: Throttle whose limit comes from an <see cref="IExpression"/>, evaluated on every message.</summary>
    public static ThrottleDefinition ThrottleExpression(this IRouteDefinition self, IExpression expression, TimeSpan period)
    {
        ArgumentNullException.ThrowIfNull(self);
        ArgumentNullException.ThrowIfNull(expression);
        return self.Throttle(ex => expression.Evaluate<int>(ex)).Period(period);
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

    // ── Route-XML Ф1.3: string overloads for the remaining lambda-only EIPs ───
    // The rule set by the 4.0 breaking bundle: the string form lives on the verb itself, never
    // under an *Expression name. Every string compiles at declaration (StringExpression), so a
    // malformed expression fails while the route is built, not on the first message.

    /// <summary>
    /// Recipient List from a route-language expression. The expression may yield a collection of
    /// URIs or a single delimited string (<c>"${header.targets}"</c> with <c>"direct:a,direct:b"</c>).
    /// </summary>
    public static IRouteDefinition RecipientList(
        this IRouteDefinition self,
        string recipientsExpression,
        string delimiter = ",",
        bool parallelProcessing = false,
        bool stopOnException = false,
        Func<IExchange, IExchange, IExchange>? aggregationStrategy = null)
    {
        ArgumentNullException.ThrowIfNull(self);
        ArgumentException.ThrowIfNullOrWhiteSpace(recipientsExpression);
        ArgumentException.ThrowIfNullOrEmpty(delimiter);
        var expression = new StringExpression(recipientsExpression);
        return self.RecipientList(
            e => SplitUris(expression.Evaluate<object>(e), delimiter),
            parallelProcessing, stopOnException, aggregationStrategy);
    }

    /// <summary>
    /// Dynamic Router from a route-language expression evaluated before every hop: the next URI,
    /// or null/empty to stop routing (<c>"property.step == 0 ? 'direct:a' : null"</c>).
    /// </summary>
    public static IRouteDefinition DynamicRouter(this IRouteDefinition self, string routingExpression)
    {
        ArgumentNullException.ThrowIfNull(self);
        ArgumentException.ThrowIfNullOrWhiteSpace(routingExpression);
        var expression = new StringExpression(routingExpression);
        return self.DynamicRouter(e =>
        {
            var uri = expression.Evaluate<object>(e)?.ToString();
            return string.IsNullOrWhiteSpace(uri) ? null : uri;
        });
    }

    /// <summary>Resequencer keyed by a route-language expression (<c>"header.seqNum"</c>), converted to a long.</summary>
    public static ResequenceDefinition Resequence(
        this IRouteDefinition self,
        string keyExpression,
        int batchSize = 100,
        TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(self);
        ArgumentException.ThrowIfNullOrWhiteSpace(keyExpression);
        var expression = new StringExpression(keyExpression);
        return self.Resequence(e => expression.Evaluate<long>(e), batchSize, timeout);
    }

    /// <summary>Debounce keyed by a route-language expression (<c>"header.deviceId"</c>).</summary>
    public static DebounceDefinition Debounce(
        this IRouteDefinition self,
        string keyExpression,
        TimeSpan quietPeriod)
    {
        ArgumentNullException.ThrowIfNull(self);
        ArgumentException.ThrowIfNullOrWhiteSpace(keyExpression);
        var expression = new StringExpression(keyExpression);
        return self.Debounce(e => expression.Evaluate<object>(e)?.ToString() ?? string.Empty, quietPeriod);
    }

    /// <summary>Idempotent consumer from a key expression and a repository registry name.</summary>
    public static IdempotentConsumerDefinition IdempotentConsumer(
        this IRouteDefinition self,
        string keyExpression,
        string repositoryName,
        bool skipDuplicate = true)
    {
        ArgumentNullException.ThrowIfNull(self);
        ArgumentException.ThrowIfNullOrWhiteSpace(keyExpression);
        var expression = new StringExpression(keyExpression);
        return self.IdempotentConsumer(
            e => expression.Evaluate<object>(e)?.ToString() ?? string.Empty,
            repositoryName, skipDuplicate);
    }

    /// <summary>
    /// Aggregate from a correlation expression, with completion by condition, size, an inactivity
    /// timeout (Apache Camel <c>completionTimeout</c>: the clock restarts on every arrival for the
    /// group), or any combination — whichever fires first completes the group. At least one
    /// completion criterion is required: an aggregate that can never complete is a leak, not a
    /// default.
    /// </summary>
    public static AggregateDefinition Aggregate(
        this IRouteDefinition self,
        string correlationExpression,
        Func<IExchange, IExchange, IExchange> aggregationStrategy,
        string? completionCondition = null,
        int? completionSize = null,
        TimeSpan? completionTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(self);
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationExpression);
        ArgumentNullException.ThrowIfNull(aggregationStrategy);
        if (completionCondition is null && completionSize is null && completionTimeout is null)
            throw new ArgumentException(
                "Aggregate needs at least one completion criterion: completionCondition, completionSize or completionTimeout.");
        if (completionSize is < 1)
            throw new ArgumentOutOfRangeException(nameof(completionSize), "completionSize must be at least 1.");

        var correlation = new StringExpression(correlationExpression);
        var conditionPredicate = completionCondition is null ? null : PredicateFactory.FromString(completionCondition);

        // completionSize rides a counter on the accumulated exchange: the strategy wrapper stamps
        // it after every merge, the predicate reads it. The __-prefixed key is invisible to
        // templates, like every engine-internal exchange property.
        const string countKey = "__redb_agg:size";
        var strategy = aggregationStrategy;
        if (completionSize is not null)
        {
            strategy = (accumulated, incoming) =>
            {
                var count = accumulated.Properties.TryGetValue(countKey, out var v) && v is int i ? i : 1;
                var merged = aggregationStrategy(accumulated, incoming);
                merged.Properties[countKey] = count + 1;
                return merged;
            };
        }

        bool Complete(IExchange e)
        {
            if (completionSize is { } size)
            {
                var count = e.Properties.TryGetValue(countKey, out var v) && v is int i ? i : 1;
                if (count >= size) return true;
            }
            return conditionPredicate?.Matches(e) == true;
        }

        var definition = self.Aggregate(
            e => correlation.Evaluate<object>(e)?.ToString() ?? string.Empty,
            strategy,
            Complete);
        definition.CompletionTimeout = completionTimeout;
        return definition;
    }

    /// <summary>
    /// Non-generic <c>OfType</c> (Route-XML Ф1.3): opens the same guarded section as
    /// <c>OfType&lt;T&gt;()</c> for a type known only at load time — following steps run only for
    /// matching bodies, exactly like the generic form. Cold path: the generic method is closed by
    /// reflection once, while the route is being built.
    /// </summary>
    public static IRouteDefinition OfType(this IRouteDefinition self, Type bodyType)
    {
        ArgumentNullException.ThrowIfNull(self);
        ArgumentNullException.ThrowIfNull(bodyType);
        var open = typeof(IRouteDefinition).GetMethod(nameof(IRouteDefinition.OfType))
            ?? throw new InvalidOperationException("IRouteDefinition.OfType<T>() not found.");
        var section = open.MakeGenericMethod(bodyType).Invoke(self, null)
            ?? throw new InvalidOperationException($"OfType({bodyType}) returned null.");
        return (IRouteDefinition)section;
    }

    /// <summary>Sticky load balancing keyed by a route-language expression (<c>"header.customerId"</c>).</summary>
    public static ILoadBalancerDefinition UseSticky(this ILoadBalancerDefinition self, string keyExpression)
    {
        ArgumentNullException.ThrowIfNull(self);
        ArgumentException.ThrowIfNullOrWhiteSpace(keyExpression);
        var expression = new StringExpression(keyExpression);
        return self.UseSticky(e => expression.Evaluate<object>(e)?.ToString() ?? string.Empty);
    }

    /// <summary>Normalizer clause from a condition string (compiled like <c>Filter(string)</c>).</summary>
    public static INormalizerDefinition When(
        this INormalizerDefinition self,
        string condition,
        Func<IExchange, object?> transform)
    {
        ArgumentNullException.ThrowIfNull(self);
        ArgumentException.ThrowIfNullOrWhiteSpace(condition);
        ArgumentNullException.ThrowIfNull(transform);
        var predicate = PredicateFactory.FromString(condition);
        return self.When(predicate.Matches, transform);
    }

    private static IEnumerable<string> SplitUris(object? value, string delimiter)
    {
        switch (value)
        {
            case null:
                return [];
            case string text:
                return text.Split(delimiter, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            case IEnumerable<string> strings:
                return strings;
            case System.Collections.IEnumerable items:
                return EnumerateUris(items);
            default:
                return [value.ToString() ?? string.Empty];
        }

        static IEnumerable<string> EnumerateUris(System.Collections.IEnumerable items)
        {
            foreach (var item in items)
                if (item?.ToString() is { Length: > 0 } uri)
                    yield return uri;
        }
    }
}

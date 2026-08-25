using Json.Schema;
using Microsoft.Extensions.Logging;
using redb.Route.Definitions;
using redb.Route.Transactions;
using redb.Route.Validation;
using redb.Route.Xslt;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Schema;

namespace redb.Route.Abstractions;

/// <summary>
/// New fluent DSL for defining a message route using the canonical ProcessorDefinition model.
/// All leaf methods return <see cref="IRouteDefinition"/> for chaining.
/// Scope-opener methods (Filter, Choice, Split, TryCatch, etc.) return the opened definition
/// and expose an End()/EndXxx() method that returns the parent definition.
/// </summary>
public interface IRouteDefinition : IProcessorDefinition
{
    // ── Identity ──

    /// <summary>Assigns a unique route identifier.</summary>
    IRouteDefinition RouteId(string routeId);

    /// <summary>Gets the assigned route ID (null if not set).</summary>
    string? GetRouteId();

    /// <summary>Sets whether this route should auto-start. Default is true.</summary>
    IRouteDefinition AutoStart(bool value = true);

    /// <summary>Gets whether this route auto-starts.</summary>
    bool GetAutoStart();

    /// <summary>Sets the per-exchange processing timeout for this route.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="timeout"/> is zero or negative (excluding <see cref="System.Threading.Timeout.InfiniteTimeSpan"/>).</exception>
    IRouteDefinition ProcessingTimeout(TimeSpan timeout);

    // ── Source ──

    /// <summary>Sets the source (consumer) endpoint URI.</summary>
    IRouteDefinition From(string uri);

    /// <summary>Gets the source endpoint URI (null if not set).</summary>
    string? GetFromUri();

    // ── Destination ──

    /// <summary>Sends the exchange to an endpoint via producer.</summary>
    IRouteDefinition To(string uri);

    /// <summary>Camel-style dynamic-to: sends the exchange to an endpoint URI computed
    /// per message from a <c>${...}</c> template (e.g. <c>.ToD("kafka://orders-${header.region}")</c>).
    /// Producers are cached per resolved URI and tracked by <see cref="Core.RouteContext"/>.</summary>
    IRouteDefinition ToD(string uriTemplate);

    /// <summary>Camel-style dynamic-to using an <see cref="IExpression"/> that yields the URI.</summary>
    IRouteDefinition ToD(IExpression uriExpression);

    /// <summary>Camel-style dynamic-to using a delegate that yields the URI per exchange.</summary>
    IRouteDefinition ToD(Func<IExchange, string> uriFactory);

    // ── Processing delegates ──

    /// <summary>Processes the exchange with a synchronous action.</summary>
    IRouteDefinition Process(Action<IExchange> action);

    /// <summary>Processes the exchange with an asynchronous action.</summary>
    IRouteDefinition Process(Func<IExchange, CancellationToken, Task> action);

    /// <summary>Processes the exchange with a pre-built processor instance.</summary>
    IRouteDefinition Process(IProcessor processor);

    // ── Transform / body ──

    /// <summary>Sets the exchange body to a static value.</summary>
    IRouteDefinition SetBody(object? value);

    /// <summary>Sets the exchange body using a factory.</summary>
    IRouteDefinition SetBody(Func<IExchange, object?> factory);

    /// <summary>Sets the exchange body using an <see cref="IExpression"/>.</summary>
    IRouteDefinition SetBody(IExpression expression);

    /// <summary>Sets the exchange body from a <c>${...}</c> template string
    /// (e.g. <c>.SetBodyExpression("Hello ${header.name}")</c>).</summary>
    IRouteDefinition SetBodyExpression(string template);

    /// <summary>Transforms the exchange body using a factory.</summary>
    IRouteDefinition Transform(Func<IExchange, object?> transform);

    /// <summary>Transforms the exchange body using an <see cref="IExpression"/>.</summary>
    IRouteDefinition Transform(IExpression expression);

    /// <summary>Removes (nulls) the exchange body.</summary>
    IRouteDefinition RemoveBody();

    // ── Headers ──

    /// <summary>Sets a header to a static value.</summary>
    IRouteDefinition SetHeader(string key, object? value);

    /// <summary>Sets a header using a factory.</summary>
    IRouteDefinition SetHeader(string key, Func<IExchange, object?> factory);

    /// <summary>Sets a header using an <see cref="IExpression"/>.</summary>
    IRouteDefinition SetHeader(string name, IExpression expression);

    /// <summary>Sets a header from a <c>${...}</c> template string
    /// (e.g. <c>.SetHeaderExpression("target", "queue-${header.tenant}")</c>).</summary>
    IRouteDefinition SetHeaderExpression(string name, string template);

    /// <summary>Removes a header.</summary>
    IRouteDefinition RemoveHeader(string key);

    // ── Properties ──

    /// <summary>Sets a property to a static value.</summary>
    IRouteDefinition SetProperty(string key, object? value);

    /// <summary>Sets a property using a factory.</summary>
    IRouteDefinition SetProperty(string key, Func<IExchange, object?> factory);

    /// <summary>Sets a property using an <see cref="IExpression"/>.</summary>
    IRouteDefinition SetProperty(string key, IExpression expression);

    /// <summary>Sets a property from a <c>${...}</c> template string.</summary>
    IRouteDefinition SetPropertyExpression(string key, string template);

    /// <summary>Removes a property.</summary>
    IRouteDefinition RemoveProperty(string key);

    // ── Logging ──

    /// <summary>Logs a static message.</summary>
    IRouteDefinition Log(string message, LogLevel level = LogLevel.Information);

    /// <summary>Logs a dynamic message from a factory.</summary>
    IRouteDefinition Log(Func<IExchange, string> messageFactory, LogLevel level = LogLevel.Information);

    // ── Stop / Throw ──

    /// <summary>Stops exchange processing.</summary>
    IRouteDefinition Stop();

    /// <summary>Rethrows the exception stored on the exchange.</summary>
    IRouteDefinition ThrowException();

    /// <summary>Throws a new exception with the given message.</summary>
    IRouteDefinition ThrowException(string message);

    /// <summary>Throws a specific pre-built exception instance.</summary>
    IRouteDefinition ThrowException(Exception exception);

    /// <summary>Instantiates and throws an exception by type and message.</summary>
    IRouteDefinition ThrowException(Type exceptionType, string message);

    /// <summary>Instantiates and throws a typed exception.</summary>
    IRouteDefinition ThrowException<TException>(string? message = null) where TException : Exception, new();

    // ── Delay / Sampling ──

    /// <summary>Delays processing by a fixed duration.</summary>
    IRouteDefinition Delay(TimeSpan duration);

    /// <summary>Delays processing by a dynamic duration computed from the exchange.</summary>
    IRouteDefinition Delay(Func<IExchange, TimeSpan> factory);

    /// <summary>Samples messages by passing every N-th message (count-based).</summary>
    IRouteDefinition Sample(long messageFrequency);

    /// <summary>Samples messages by period — at most one message per <paramref name="period"/>.</summary>
    IRouteDefinition Sample(TimeSpan period);

    // ── Stream caching ──

    /// <summary>
    /// Wraps a forward-only <see cref="System.IO.Stream"/> body with a seekable cache so
    /// subsequent steps can re-read it multiple times. Non-Stream bodies pass through unchanged.
    /// </summary>
    /// <param name="spoolThreshold">Bytes threshold before spooling to a temp file (null = default 128 KB).</param>
    IRouteDefinition StreamCaching(long? spoolThreshold = null);

    // ── Validation ──

    /// <summary>Validates the exchange using the provided <see cref="IMessageValidator"/> instance.</summary>
    IRouteDefinition Validate(IMessageValidator validator, bool throwOnFailure = true);

    /// <summary>Validates the exchange using a predicate function.</summary>
    IRouteDefinition Validate(Func<IExchange, bool> predicate, string errorMessage = "Validation failed", bool throwOnFailure = true);

    /// <summary>Validates the exchange body against a JSON Schema provided as a string.</summary>
    IRouteDefinition ValidateJsonSchema(string schemaJson, bool throwOnFailure = true);

    /// <summary>Validates the exchange body against a pre-parsed <see cref="JsonSchema"/>.</summary>
    IRouteDefinition ValidateJsonSchema(JsonSchema schema, bool throwOnFailure = true);

    /// <summary>Validates the exchange body against an XSD schema string.</summary>
    IRouteDefinition ValidateXsd(string xsdContent, bool throwOnFailure = true);

    /// <summary>Validates the exchange body against an XSD schema string with an explicit target namespace.</summary>
    IRouteDefinition ValidateXsd(string? targetNamespace, string xsdContent, bool throwOnFailure = true);

    /// <summary>Validates the exchange body against a pre-built <see cref="XmlSchemaSet"/>.</summary>
    IRouteDefinition ValidateXsd(XmlSchemaSet schemaSet, bool throwOnFailure = true);

    // ── XSLT ──

    /// <summary>Apache Camel <c>xslt:</c> parity: transform the body through an XSLT stylesheet file.</summary>
    IRouteDefinition Xslt(string stylesheetPath, XsltOutput output = XsltOutput.String, bool failOnNullBody = true, bool allowTemplateFromHeader = false);

    /// <summary>Transform the body through an inline XSLT stylesheet document.</summary>
    IRouteDefinition XsltContent(string stylesheetXml, XsltOutput output = XsltOutput.String, bool failOnNullBody = true, bool allowTemplateFromHeader = false);

    // ── Control Bus ──

    /// <summary>Apache Camel <c>controlbus:</c> parity: manage a route at runtime (start/stop/suspend/resume/restart/status/stats/fail).</summary>
    IRouteDefinition ControlBus(redb.Route.ControlBus.ControlBusAction action, string routeId, bool async = false);

    // ── Serialization ──

    /// <summary>Marshals (serializes) the exchange body using the specified serializer type.</summary>
    IRouteDefinition Marshal(Type serializerType);

    /// <summary>Marshals (serializes) the exchange body using the specified serializer type.</summary>
    IRouteDefinition Marshal<TSerializer>() where TSerializer : class;

    /// <summary>Unmarshals (deserializes) the exchange body to <paramref name="targetType"/> using the specified serializer type.</summary>
    IRouteDefinition Unmarshal(Type serializerType, Type targetType);

    /// <summary>Unmarshals (deserializes) the exchange body using the specified serializer and target types.</summary>
    IRouteDefinition Unmarshal<TSerializer, TTarget>() where TSerializer : class;

    /// <summary>Apache Camel parity: unmarshal the body to <typeparamref name="T"/> using the data format
    /// registry entry resolved from the incoming message ContentType.</summary>
    IRouteDefinition Unmarshal<T>();

    /// <summary>Apache Camel parity: convert the exchange body to <typeparamref name="T"/>.</summary>
    IRouteDefinition ConvertBody<T>();

    /// <summary>Apache Camel parity: convert the exchange body to <paramref name="targetType"/>.</summary>
    IRouteDefinition ConvertBody(Type targetType);

    /// <summary>Apache Camel parity: open a typed scope auto-converting the body to <typeparamref name="T"/>.</summary>
    OfTypeDefinition<T> OfType<T>();

    // ── Transactions ──

    /// <summary>Begins an imperative transaction scope on the exchange (default Required policy).</summary>
    IRouteDefinition BeginTransaction();

    /// <summary>Begins an imperative transaction scope with the specified <see cref="TransactionPolicy"/>.</summary>
    IRouteDefinition BeginTransaction(TransactionPolicy policy);

    /// <summary>Commits the imperative transaction scope and all deferred actions.</summary>
    IRouteDefinition CommitTransaction();

    /// <summary>Rolls back the imperative transaction scope and all deferred actions.</summary>
    IRouteDefinition RollbackTransaction();

    /// <summary>
    /// Opens a declarative transaction scope. All child steps execute inside a
    /// <c>TransactedProcessor</c> that auto-commits on success and auto-rolls-back on failure.
    /// Close the scope with <see cref="Definitions.TransactionDefinition.EndTransaction"/> or
    /// the universal <see cref="IRouteScope.End"/>.
    /// </summary>
    Definitions.TransactionDefinition Transaction(TransactionPolicy? policy = null);

    // ── Replay checkpoints ──

    /// <summary>
    /// Opens a replay checkpoint (save-point). The steps that follow are the marker's body (the tail
    /// re-run on replay); the exchange is snapshotted into <c>route.checkpoint</c> on each pass. At
    /// the start of a route no <c>End</c> is needed; otherwise close with
    /// <see cref="Definitions.ReplayableDefinition.EndReplayable"/> or use the lambda-body overload.
    /// </summary>
    Definitions.ReplayableDefinition Replayable(string name, bool exposed = false);

    /// <summary>Lambda-body form of <see cref="Replayable(string, bool)"/>; the chain stays on the current definition.</summary>
    IRouteDefinition Replayable(string name, Action<Definitions.ReplayableDefinition> body, bool exposed = false);

    // ── Telemetry ──

    /// <summary>
    /// Opens a tracing scope. All child steps run inside a single Activity span named <paramref name="operationName"/>.
    /// Close with <see cref="Definitions.TracedDefinition.EndTraced"/> or <see cref="IRouteScope.End"/>.
    /// </summary>
    Definitions.TracedDefinition Traced(string operationName);

    /// <summary>
    /// Inline tracing — wraps a single synchronous action in an Activity span named <paramref name="operationName"/>.
    /// </summary>
    IRouteDefinition Traced(string operationName, Action<IExchange> action);

    /// <summary>
    /// Inline tracing — wraps an asynchronous action in an Activity span named <paramref name="operationName"/>.
    /// </summary>
    IRouteDefinition Traced(string operationName, Func<IExchange, CancellationToken, Task> action);

    /// <summary>
    /// Inline tracing — wraps an <see cref="IProcessor"/> in an Activity span named <paramref name="operationName"/>.
    /// </summary>
    IRouteDefinition Traced(string operationName, IProcessor processor);

    /// <summary>
    /// Opens a metrics scope. All child steps are measured by <c>MeteredStepProcessor</c>
    /// tagged with <paramref name="stepName"/>.
    /// Close with <see cref="Definitions.MeteredDefinition.EndMetered"/> or <see cref="IRouteScope.End"/>.
    /// </summary>
    Definitions.MeteredDefinition Metered(string stepName);

    /// <summary>
    /// Inline metering — wraps a single synchronous action in a MeteredStepProcessor tagged with <paramref name="stepName"/>.
    /// </summary>
    IRouteDefinition Metered(string stepName, Action<IExchange> action);

    /// <summary>
    /// Inline metering — wraps an asynchronous action in a MeteredStepProcessor tagged with <paramref name="stepName"/>.
    /// </summary>
    IRouteDefinition Metered(string stepName, Func<IExchange, CancellationToken, Task> action);

    /// <summary>
    /// Inline metering — wraps an <see cref="IProcessor"/> in a MeteredStepProcessor tagged with <paramref name="stepName"/>.
    /// </summary>
    IRouteDefinition Metered(string stepName, IProcessor processor);

    // ── Enrichment ──

    /// <summary>Sends exchange to a URI and continues (wire tap).</summary>
    IRouteDefinition WireTap(string uri);

    /// <summary>Sends a wire-tap copy to a URI, calling <paramref name="onPrepare"/> on the clone before dispatching.</summary>
    IRouteDefinition WireTap(string uri, Action<IExchange> onPrepare);

    /// <summary>Sends a wire-tap copy to a URI, replacing the clone's body with the result of <paramref name="newBodyFactory"/>.</summary>
    IRouteDefinition WireTap(string uri, Func<IExchange, object?> newBodyFactory);

    /// <summary>Sends a wire-tap copy to a URI with both an <paramref name="onPrepare"/> callback and a body replacement factory.</summary>
    IRouteDefinition WireTap(string uri, Action<IExchange> onPrepare, Func<IExchange, object?> newBodyFactory);

    /// <summary>Wire-tap to a dynamic URI computed per message from a factory.
    /// Producers are cached per resolved URI.</summary>
    IRouteDefinition WireTap(Func<IExchange, string> uriFactory);

    /// <summary>Dynamic-URI wire-tap with an <paramref name="onPrepare"/> callback.</summary>
    IRouteDefinition WireTap(Func<IExchange, string> uriFactory, Action<IExchange> onPrepare);

    /// <summary>Dynamic-URI wire-tap with a body replacement factory.</summary>
    IRouteDefinition WireTap(Func<IExchange, string> uriFactory, Func<IExchange, object?> newBodyFactory);

    /// <summary>Dynamic-URI wire-tap with both an <paramref name="onPrepare"/> callback and a body factory.</summary>
    IRouteDefinition WireTap(Func<IExchange, string> uriFactory, Action<IExchange> onPrepare, Func<IExchange, object?> newBodyFactory);

    /// <summary>Content enricher — calls an endpoint and merges the result.</summary>
    IRouteDefinition Enrich(string resourceUri, Func<IExchange, IExchange, IExchange> mergeStrategy);

    /// <summary>Content enricher with a dynamic endpoint URI computed per message.</summary>
    IRouteDefinition Enrich(Func<IExchange, string> uriFactory, Func<IExchange, IExchange, IExchange> mergeStrategy);

    /// <summary>Apache Camel parity: poll an external endpoint and merge the polled result.</summary>
    IRouteDefinition PollEnrich(
        string resourceUri,
        Func<IExchange, IExchange?, IExchange> mergeStrategy,
        TimeSpan? timeout = null);

    /// <summary>Poll enricher with a dynamic endpoint URI computed per message.</summary>
    IRouteDefinition PollEnrich(
        Func<IExchange, string> uriFactory,
        Func<IExchange, IExchange?, IExchange> mergeStrategy,
        TimeSpan? timeout = null);

    /// <summary>Apache Camel parity: route to a list of recipient URIs computed at runtime.</summary>
    IRouteDefinition RecipientList(
        Func<IExchange, IEnumerable<string>> recipientListFactory,
        bool parallelProcessing = false,
        bool stopOnException = false,
        Func<IExchange, IExchange, IExchange>? aggregationStrategy = null);

    /// <summary>Apache Camel parity: iteratively route to URIs returned by a routing function.</summary>
    IRouteDefinition DynamicRouter(Func<IExchange, string?> routingFunction);

    /// <summary>Apache Camel parity: Routing Slip — pipe the exchange through a list of endpoints
    /// computed once up front (Out→In between hops).</summary>
    IRouteDefinition RoutingSlip(Func<IExchange, IEnumerable<string>> slipFactory, bool ignoreInvalidEndpoints = false);

    /// <summary>Apache Camel parity: Routing Slip from an expression yielding a delimited URI string.</summary>
    IRouteDefinition RoutingSlip(IExpression slip, string uriDelimiter = ",", bool ignoreInvalidEndpoints = false);

    /// <summary>Apache Camel parity: Routing Slip from a <c>${...}</c> template resolving to a delimited URI string.</summary>
    IRouteDefinition RoutingSlip(string slipTemplate, string uriDelimiter = ",", bool ignoreInvalidEndpoints = false);

    /// <summary>Apache Camel parity: open a Resequencer scope. Child outputs receive exchanges
    /// in the order determined by <paramref name="keySelector"/>.</summary>
    ResequenceDefinition Resequence(
        Func<IExchange, long> keySelector,
        int batchSize = 100,
        TimeSpan? timeout = null);

    // ── Scope openers ──

    /// <summary>
    /// Opens a Filter scope. Only exchanges matching the predicate will be processed by steps
    /// inside the scope. Close with <see cref="FilterDefinition.EndFilter"/>.
    /// </summary>
    FilterDefinition Filter(Func<IExchange, bool> predicate);

    /// <summary>
    /// Opens a Filter scope using an <see cref="IExpression"/> predicate.
    /// Close with <see cref="FilterDefinition.EndFilter"/>.
    /// </summary>
    FilterDefinition Filter(IExpression expression);

    /// <summary>
    /// Opens an Idempotent Consumer scope. Duplicate exchanges (same key) are skipped.
    /// Close with <see cref="IdempotentConsumerDefinition.EndIdempotentConsumer"/>.
    /// </summary>
    IdempotentConsumerDefinition IdempotentConsumer(
        IIdempotentRepository repository,
        Func<IExchange, string> keyExtractor,
        bool skipDuplicate = true);

    /// <summary>
    /// Claim Check step (Hohpe/Woolf). Stores the body away and lets a short claim key travel
    /// the route in its place, or restores the body from such a key.
    /// </summary>
    /// <param name="operation">
    /// <see cref="ClaimCheckOperation.Set"/> / <see cref="ClaimCheckOperation.Get"/> /
    /// <see cref="ClaimCheckOperation.GetAndRemove"/> address an explicit key;
    /// <see cref="ClaimCheckOperation.Push"/> / <see cref="ClaimCheckOperation.Pop"/> use an
    /// exchange-scoped stack and need none.
    /// </param>
    /// <param name="key">Explicit claim key for Set/Get/GetAndRemove. Ignored by Push/Pop.</param>
    /// <param name="ttl">Time-to-live for stored data. Used by Set/Push.</param>
    /// <param name="repositoryName">
    /// Logical name of a repository registered with
    /// <c>context.AddClaimCheckRepository(name, repository)</c>. Null uses the context default.
    /// </param>
    IRouteDefinition ClaimCheck(
        ClaimCheckOperation operation,
        string? key = null,
        TimeSpan? ttl = null,
        string? repositoryName = null);

    /// <summary>
    /// Claim Check step against an explicit repository instance.
    /// </summary>
    IRouteDefinition ClaimCheck(
        IClaimCheckRepository repository,
        ClaimCheckOperation operation,
        string? key = null,
        TimeSpan? ttl = null);

    /// <summary>
    /// Opens a Choice (content-based router) scope.
    /// Add branches with <c>.When()</c> / <c>.Otherwise()</c>, close with <c>.EndChoice()</c>.
    /// </summary>
    ChoiceDefinition Choice();

    /// <summary>
    /// Opens a Split scope. The splitter function splits the exchange body into parts;
    /// each part is processed through the scope pipeline.
    /// Close with <see cref="SplitDefinition.EndSplit"/>.
    /// </summary>
    SplitDefinition Split(Func<IExchange, IEnumerable<object?>> splitter);

    /// <summary>
    /// Opens a Split scope using an expression to produce the split parts.
    /// The expression must evaluate to <see cref="IEnumerable{T}"/>.
    /// Close with <see cref="SplitDefinition.EndSplit"/>.
    /// </summary>
    SplitDefinition Split(IExpression expression);

    /// <summary>
    /// Opens a streaming Split scope backed by an async enumerable (no parallel / aggregation).
    /// Parts are processed one at a time without buffering.
    /// Close with <see cref="SplitDefinition.EndSplit"/>.
    /// </summary>
    SplitDefinition Split(Func<IExchange, IAsyncEnumerable<object?>> splitter);

    /// <summary>
    /// Opens a Multicast scope (fan-out). Add targets with <c>.To()</c> / <c>.Process()</c>.
    /// Close with <see cref="MulticastDefinition.EndMulticast"/>.
    /// </summary>
    MulticastDefinition Multicast();

    /// <summary>
    /// Opens a Threads scope (Camel-style concurrency handoff). The child pipeline runs on a bounded
    /// pool of <paramref name="poolSize"/> persistent workers, so a serial polling consumer can process
    /// up to <paramref name="poolSize"/> exchanges concurrently. Async boundary — does not extend the
    /// caller's transaction; ordering is not preserved when <paramref name="poolSize"/> &gt; 1.
    /// Close with <see cref="ThreadsDefinition.EndThreads"/>.
    /// </summary>
    /// <param name="poolSize">Number of concurrent workers (must be &gt;= 1).</param>
    ThreadsDefinition Threads(int poolSize);

    /// <summary>
    /// Opens a TryCatch scope. Build the try body with leaf methods,
    /// then open catch branches with <c>.Catch&lt;T&gt;()</c> and optionally <c>.Finally()</c>.
    /// Close with <c>.EndTryCatch()</c>.
    /// </summary>
    TryCatchDefinition TryCatch();

    /// <summary>
    /// Opens an Aggregate scope. Collects exchanges by correlation key and forwards completed
    /// aggregates to the body pipeline. Close with <c>.EndAggregate()</c>.
    /// </summary>
    AggregateDefinition Aggregate(
        Func<IExchange, string> correlationKey,
        Func<IExchange, IExchange, IExchange> aggregationStrategy,
        Func<IExchange, bool> completionPredicate);

    /// <summary>
    /// Opens a Throttle scope that limits exchange throughput to <paramref name="maxPerPeriod"/>
    /// per period. Close with <c>.EndThrottle()</c>.
    /// </summary>
    ThrottleDefinition Throttle(int maxPerPeriod);

    /// <summary>
    /// Opens a keyed Throttle scope that limits throughput per unique key extracted from the exchange.
    /// Close with <c>.EndKeyedThrottle()</c>.
    /// </summary>
    KeyedThrottleDefinition Throttle(
        Func<IExchange, string> keyExtractor,
        int maxPerPeriod,
        TimeSpan? period = null);

    /// <summary>
    /// Opens a CircuitBreaker scope. Configure options before adding downstream steps.
    /// Optionally add a fallback with <c>.OnFallback()</c>.
    /// Close with <c>.EndCircuitBreaker()</c>.
    /// </summary>
    CircuitBreakerDefinition CircuitBreaker();

    /// <summary>
    /// Opens a Debounce scope. Suppresses rapid-fire messages per key, forwarding only the last
    /// exchange after <paramref name="quietPeriod"/> of silence. Close with <c>.EndDebounce()</c>.
    /// </summary>
    DebounceDefinition Debounce(Func<IExchange, string> keyExtractor, TimeSpan quietPeriod);

    /// <summary>
    /// Opens a Loop scope that repeats the body pipeline <paramref name="count"/> times.
    /// Close with <see cref="LoopDefinition.EndLoop"/>.
    /// </summary>
    /// <param name="count">Fixed number of iterations (non-negative).</param>
    /// <param name="copy">If true each iteration receives a clone of the original exchange.</param>
    /// <param name="shareScope">If true (default) clone iterations share the parent DI scope.</param>
    LoopDefinition Loop(int count, bool copy = false, bool shareScope = true);

    /// <summary>
    /// Opens a Loop scope that repeats the body pipeline while <paramref name="condition"/> holds.
    /// Close with <see cref="LoopDefinition.EndLoop"/>.
    /// </summary>
    LoopDefinition Loop(Func<IExchange, bool> condition, bool copy = false, bool shareScope = true);

    /// <summary>
    /// Opens a Loop scope that repeats the body pipeline a number of times resolved from the exchange at runtime.
    /// Close with <see cref="LoopDefinition.EndLoop"/>.
    /// </summary>
    LoopDefinition Loop(Func<IExchange, int> countFactory, bool copy = false, bool shareScope = true);

    // ── Bean / Service Activator ──

    /// <summary>Resolves <typeparamref name="TService"/> from the exchange's ServiceProvider and invokes <paramref name="method"/>.</summary>
    IRouteDefinition Bean<TService>(Func<TService, IExchange, CancellationToken, Task> method);

    /// <summary>Resolves <typeparamref name="TService"/> from the exchange's ServiceProvider and invokes <paramref name="method"/> (no cancellation token).</summary>
    IRouteDefinition Bean<TService>(Func<TService, IExchange, Task> method);

    /// <summary>Resolves <typeparamref name="TService"/> from the exchange's ServiceProvider and invokes a synchronous <paramref name="method"/>.</summary>
    IRouteDefinition Bean<TService>(Action<TService, IExchange> method);

    // ── Scatter-Gather ──

    /// <summary>
    /// Adds a scatter-gather step with static recipients and the specified aggregation strategy.
    /// </summary>
    IRouteDefinition ScatterGather(
        Func<IExchange, IExchange, IExchange> aggregationStrategy,
        params string[] recipients);

    /// <summary>
    /// Adds a scatter-gather step configured via a builder callback.
    /// </summary>
    IRouteDefinition ScatterGather(Action<IScatterGatherDefinition> configure);

    // ── Saga ──

    /// <summary>
    /// Adds a saga step configured via a builder callback.
    /// </summary>
    IRouteDefinition Saga(Action<ISagaDefinition> configure);

    /// <summary>
    /// Opens a saga scope. Add steps with <c>SagaStep()</c> / <c>OnSagaCompletion()</c>,
    /// then close with <see cref="Definitions.SagaDefinition.EndSaga"/> or
    /// <see cref="IRouteScope.End"/>.
    /// </summary>
    Definitions.SagaDefinition Saga();

    // ── LoadBalance ──

    /// <summary>
    /// Adds a load-balance step with the specified strategy and endpoint URIs.
    /// </summary>
    IRouteDefinition LoadBalance(ILoadBalancerStrategy strategy, params string[] uris);

    /// <summary>
    /// Adds a load-balance step configured via a builder callback.
    /// </summary>
    IRouteDefinition LoadBalance(Action<ILoadBalancerDefinition> configure);

    // ── Normalizer ──

    /// <summary>
    /// Adds a normalizer step configured via a builder callback.
    /// Each when-clause transforms messages matching its predicate.
    /// </summary>
    IRouteDefinition Normalize(Action<INormalizerDefinition> configure);

    // ── Exception handling ──

    /// <summary>Marks the exchange exception as handled: clears the exception and sets <c>ExceptionHandled = true</c>.</summary>
    IRouteDefinition ExceptionHandled();

    /// <summary>Rolls back all transacted actions registered on the exchange and sets the <c>RollbackOnly</c> flag.</summary>
    IRouteDefinition RollbackAll();

    /// <summary>
    /// Opens an OnException scope for <typeparamref name="TException"/>.
    /// Configure redelivery/handling, then close with <c>.EndOnException()</c> or <c>.End()</c>.
    /// The child steps (added before closing) form the body to protect.
    /// </summary>
    Definitions.OnExceptionDefinition OnException<TException>() where TException : Exception;

    /// <summary>
    /// Opens an OnException scope for multiple exception types.
    /// Configure redelivery/handling, then close with <c>.EndOnException()</c> or <c>.End()</c>.
    /// The child steps (added before closing) form the body to protect.
    /// </summary>
    /// <exception cref="ArgumentException">Thrown when <paramref name="exceptionTypes"/> is empty or contains a non-Exception type.</exception>
    Definitions.OnExceptionDefinition OnException(params Type[] exceptionTypes);

    // ── Route-level policy ──

    /// <summary>Sets whether this route participates in cluster-aware routing. Default: false.</summary>
    IRouteDefinition Cluster(bool value = true);

    /// <summary>Gets whether this route participates in cluster-aware routing.</summary>
    bool GetCluster();

    /// <summary>Apache Camel parity: enable/disable per-node message history for this route, overriding
    /// the global <c>RouteEngineOptions.EnableMessageHistory</c>.</summary>
    IRouteDefinition MessageHistory(bool value = true);

    /// <summary>Gets the route-level message-history override (<c>null</c> = inherit the global setting).</summary>
    bool? GetMessageHistory();

    /// <summary>Sets an explicit route policy, overriding any factory-resolved policy.</summary>
    IRouteDefinition RoutePolicy(IRoutePolicy policy);

    /// <summary>Gets the explicitly set route policy, or null if not set.</summary>
    IRoutePolicy? GetRoutePolicy();

    // ── Rich logging scope ──

    /// <summary>
    /// Opens a rich-log scope at the given <paramref name="level"/>.
    /// Add messages/headers/properties, then close with <c>.EndLog()</c> or <c>.End()</c>.
    /// </summary>
    Definitions.RichLogScopeDefinition Log(LogLevel level);
}

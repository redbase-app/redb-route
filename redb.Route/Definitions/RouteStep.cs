using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;

namespace redb.Route.Definitions;

/// <summary>
/// Read-only projection of a single step in the route AST, exposed via
/// <see cref="RouteDefinition.Steps"/>. Built from <see cref="IProcessorDefinition"/>
/// nodes by <see cref="RouteStepProjection"/> for diagnostics, validation and tooling.
/// Not used for compilation — that goes through <c>IProcessorDefinition.CreateProcessor</c>.
/// </summary>
public abstract record RouteStep;

/// <summary>Step: set the source endpoint.</summary>
/// <param name="Uri">Source endpoint URI.</param>
public record FromStep(string Uri) : RouteStep;

/// <summary>Step: send to a target endpoint.</summary>
/// <param name="Uri">Target endpoint URI.</param>
public record ToStep(string Uri) : RouteStep;

/// <summary>Step: filter by predicate.</summary>
/// <param name="Predicate">Filter predicate.</param>
/// <param name="SubSteps">
/// Optional explicit body. When non-null, the filter's inner pipeline is taken from these
/// sub-steps (Camel scope-form: <c>Filter().To(...).EndFilter()</c>) instead of consuming
/// the surrounding pipeline's tail.
/// </param>
public record FilterStep(
    Func<Abstractions.IExchange, bool> Predicate,
    IReadOnlyList<RouteStep>? SubSteps = null) : RouteStep;

/// <summary>Step: process via async delegate.</summary>
/// <param name="Action">Async processing delegate.</param>
public record ProcessAsyncStep(Func<Abstractions.IExchange, CancellationToken, Task> Action) : RouteStep;

/// <summary>Step: process via sync delegate.</summary>
/// <param name="Action">Synchronous processing action.</param>
public record ProcessSyncStep(Action<Abstractions.IExchange> Action) : RouteStep;

/// <summary>Step: process via IProcessor instance.</summary>
/// <param name="Processor">Processor instance.</param>
public record ProcessInstanceStep(Abstractions.IProcessor Processor) : RouteStep;

/// <summary>Step: set body to a static value.</summary>
/// <param name="Value">New body value.</param>
public record SetBodyStaticStep(object? Value) : RouteStep;

/// <summary>Step: set body via factory.</summary>
/// <param name="Factory">Factory that produces the new body.</param>
public record SetBodyFactoryStep(Func<Abstractions.IExchange, object?> Factory) : RouteStep;

/// <summary>Step: transform body via function (alias for SetBodyFactory but semantically distinct).</summary>
/// <param name="Transform">Transform function.</param>
public record TransformStep(Func<Abstractions.IExchange, object?> Transform) : RouteStep;

/// <summary>Step: set header to a static value.</summary>
/// <param name="Key">Header key.</param>
/// <param name="Value">Header value.</param>
public record SetHeaderStaticStep(string Key, object? Value) : RouteStep;

/// <summary>Step: set header via factory.</summary>
/// <param name="Key">Header key.</param>
/// <param name="Factory">Factory that produces the header value.</param>
public record SetHeaderFactoryStep(string Key, Func<Abstractions.IExchange, object?> Factory) : RouteStep;

/// <summary>Step: remove a header.</summary>
/// <param name="Key">Header key to remove.</param>
public record RemoveHeaderStep(string Key) : RouteStep;

/// <summary>Step: set a property to a static value.</summary>
/// <param name="Key">Property key.</param>
/// <param name="Value">Property value.</param>
public record SetPropertyStaticStep(string Key, object? Value) : RouteStep;

/// <summary>Step: set a property via factory.</summary>
/// <param name="Key">Property key.</param>
/// <param name="Factory">Factory that produces the property value.</param>
public record SetPropertyFactoryStep(string Key, Func<Abstractions.IExchange, object?> Factory) : RouteStep;

/// <summary>Step: set a property via expression.</summary>
/// <param name="Key">Property key.</param>
/// <param name="Expression">Expression that produces the property value.</param>
public record SetPropertyExpressionStep(string Key, Abstractions.IExpression Expression) : RouteStep;

/// <summary>Step: set a property via string expression template.</summary>
/// <param name="Key">Property key.</param>
/// <param name="Expression">Expression template string with <c>${...}</c> placeholders.</param>
public record SetPropertyStringExpressionStep(string Key, string Expression) : RouteStep;

/// <summary>Step: content-based router.</summary>
/// <param name="WhenClauses">Ordered when-clauses (lambda-based).</param>
/// <param name="OtherwiseSteps">Fallback steps (null if no otherwise).</param>
/// <param name="PredicateClauses">Ordered when-clauses using IPredicate.</param>
/// <param name="ExpressionClauses">Ordered when-clauses using string expressions.</param>
public record ChoiceStep(
    IReadOnlyList<ChoiceWhenClause> WhenClauses,
    IReadOnlyList<RouteStep>? OtherwiseSteps,
    IReadOnlyList<ChoiceWhenPredicateClause>? PredicateClauses = null,
    IReadOnlyList<ChoiceWhenExpressionClause>? ExpressionClauses = null) : RouteStep;

/// <summary>A single when-clause in a choice step.</summary>
/// <param name="Predicate">Condition to evaluate.</param>
/// <param name="Steps">Steps for this branch.</param>
public record ChoiceWhenClause(Func<Abstractions.IExchange, bool> Predicate, IReadOnlyList<RouteStep> Steps);

/// <summary>Step: multicast to multiple endpoints.</summary>
/// <param name="Uris">Target endpoint URIs.</param>
/// <param name="ParallelProcessing">Whether to process in parallel.</param>
/// <param name="AggregationStrategy">Optional pair-wise aggregation strategy.</param>
/// <param name="StopOnException">Whether to stop on the first exception.</param>
/// <param name="Timeout">Timeout for the operation (TimeSpan.Zero = no timeout).</param>
/// <param name="MaxDegreeOfParallelism">Max concurrent tasks (0 = processor count).</param>
public record MulticastStep(
    string[] Uris,
    bool ParallelProcessing = true,
    Func<Abstractions.IExchange, Abstractions.IExchange, Abstractions.IExchange>? AggregationStrategy = null,
    bool StopOnException = false,
    TimeSpan Timeout = default,
    int MaxDegreeOfParallelism = 0) : RouteStep;

/// <summary>Step: fire-and-forget clone to tap endpoint.</summary>
/// <param name="Uri">Tap endpoint URI.</param>
/// <param name="OnPrepare">Optional callback to modify the cloned exchange before tapping.</param>
/// <param name="NewBodyFactory">Optional factory to replace the body on the tapped clone.</param>
public record WireTapStep(
    string Uri,
    Action<Abstractions.IExchange>? OnPrepare = null,
    Func<Abstractions.IExchange, object?>? NewBodyFactory = null) : RouteStep;

/// <summary>Step: split body into multiple exchanges.</summary>
/// <param name="Splitter">Function that splits the body.</param>
/// <param name="SubSteps">Optional sub-route steps for each split part.</param>
/// <param name="ParallelProcessing">Whether to process parts in parallel.</param>
/// <param name="MaxDegreeOfParallelism">Max concurrent tasks (0 = processor count).</param>
/// <param name="AggregationStrategy">
/// Optional pair-wise aggregation strategy (Camel-compatible): for the first part
/// <c>oldExchange == null</c> so the strategy can perform a seed/wrap.
/// </param>
/// <param name="StopOnException">Whether to stop on the first exception.</param>
/// <param name="Timeout">Timeout for the operation (TimeSpan.Zero = no timeout).</param>
/// <param name="ParallelAggregate">When true and ParallelProcessing is true, aggregation runs in-task under a lock (strategy must be thread-safe).</param>
/// <param name="AggregateOnException">When true, failed split exchanges still feed into the aggregation strategy.</param>
public record SplitStep(
    Func<Abstractions.IExchange, IEnumerable<object?>> Splitter,
    IReadOnlyList<RouteStep>? SubSteps,
    bool ParallelProcessing = false,
    int MaxDegreeOfParallelism = 0,
    Func<Abstractions.IExchange?, Abstractions.IExchange, Abstractions.IExchange>? AggregationStrategy = null,
    bool StopOnException = true,
    TimeSpan Timeout = default,
    bool ParallelAggregate = false,
    bool AggregateOnException = false) : RouteStep;

/// <summary>Step: aggregate exchanges by correlation key.</summary>
/// <param name="CorrelationKey">Correlation key extractor.</param>
/// <param name="AggregationStrategy">Merge function (old, new) → merged.</param>
/// <param name="CompletionPredicate">Completion predicate.</param>
public record AggregateStep(
    Func<Abstractions.IExchange, string> CorrelationKey,
    Func<Abstractions.IExchange, Abstractions.IExchange, Abstractions.IExchange> AggregationStrategy,
    Func<Abstractions.IExchange, bool> CompletionPredicate) : RouteStep;

/// <summary>Step: count-based loop.</summary>
/// <param name="Count">Number of iterations.</param>
/// <param name="BodySteps">Steps for the loop body.</param>
/// <param name="Copy">If true, each iteration receives a clone of the original exchange.</param>
/// <param name="ShareScope">If true (default), copy-mode iterations share the parent's DI scope.</param>
public record LoopCountStep(int Count, IReadOnlyList<RouteStep> BodySteps, bool Copy = false, bool ShareScope = true) : RouteStep;

/// <summary>Step: predicate-based loop.</summary>
/// <param name="Condition">Predicate evaluated before each iteration.</param>
/// <param name="BodySteps">Steps for the loop body.</param>
/// <param name="Copy">If true, each iteration receives a clone of the original exchange.</param>
/// <param name="ShareScope">If true (default), copy-mode iterations share the parent's DI scope.</param>
public record LoopWhileStep(Func<Abstractions.IExchange, bool> Condition, IReadOnlyList<RouteStep> BodySteps, bool Copy = false, bool ShareScope = true) : RouteStep;

/// <summary>Step: count-based loop where count is resolved from a string expression (<c>${header.loopCount}</c>).</summary>
/// <param name="Expression">Expression string resolving to an integer count.</param>
/// <param name="BodySteps">Steps for the loop body.</param>
/// <param name="Copy">If true, each iteration receives a clone of the original exchange.</param>
/// <param name="ShareScope">If true (default), copy-mode iterations share the parent's DI scope.</param>
public record LoopCountExpressionStep(string Expression, IReadOnlyList<RouteStep> BodySteps, bool Copy = false, bool ShareScope = true) : RouteStep;

/// <summary>Step: delay processing by a fixed duration.</summary>
/// <param name="Duration">Delay duration.</param>
public record DelayStep(TimeSpan Duration) : RouteStep;

/// <summary>Step: delay processing by a dynamic duration.</summary>
/// <param name="DurationFactory">Function that computes the delay from the exchange.</param>
public record DelayFactoryStep(Func<Abstractions.IExchange, TimeSpan> DurationFactory) : RouteStep;

/// <summary>Step: delay processing by a duration resolved from a string expression (<c>${header.delay}</c>).</summary>
/// <param name="Expression">Expression string resolving to a TimeSpan or milliseconds number.</param>
public record DelayExpressionStep(string Expression) : RouteStep;

/// <summary>Step: try-catch error handling wrapper.</summary>
/// <param name="BodySteps">Steps wrapped in try.</param>
/// <param name="CatchClauses">Ordered catch clauses.</param>
/// <param name="FinallySteps">Optional finally steps.</param>
public record TryCatchStep(
    IReadOnlyList<RouteStep> BodySteps,
    IReadOnlyList<TryCatchClause> CatchClauses,
    IReadOnlyList<RouteStep>? FinallySteps) : RouteStep;

/// <summary>A catch clause within a TryCatchStep.</summary>
/// <param name="ExceptionType">Exception type to catch.</param>
/// <param name="HandlerSteps">Handler steps.</param>
public record TryCatchClause(Type ExceptionType, IReadOnlyList<RouteStep> HandlerSteps);

/// <summary>Step: global exception handler for the route.</summary>
/// <param name="Handlers">Exception handlers.</param>
public record OnExceptionStep(IReadOnlyList<OnExceptionHandler> Handlers) : RouteStep;

/// <summary>A single exception handler in OnExceptionStep.</summary>
/// <param name="ExceptionType">Exception type to handle.</param>
/// <param name="HandlerSteps">Handler steps.</param>
/// <param name="MaxRedeliveries">Max redelivery attempts.</param>
/// <param name="RedeliveryDelay">Delay between redelivery attempts (default: 1 second).</param>
/// <param name="BackoffMultiplier">Backoff multiplier for redelivery delay (default: 1.0 = fixed).</param>
/// <param name="UseExponentialBackoff">Whether to use exponential backoff (default: false).</param>
/// <param name="Handled">If true, exception is marked handled and not re-thrown.</param>
/// <param name="Continued">If true, pipeline continues after the handler runs.</param>
/// <param name="OnWhenPredicate">Optional predicate — handler fires only when predicate returns true.</param>
/// <param name="RetryAttemptedLogLevel">Log level for retry attempt messages (default: Warning).</param>
/// <param name="RetriesExhaustedLogLevel">Log level when all retries fail (default: Error).</param>
/// <param name="OnExceptionOccurredCallback">Optional callback invoked each time the exception occurs.</param>
/// <param name="RetryWhilePredicate">Optional predicate — retry continues while true.</param>
/// <param name="OnRedeliveryCallback">Optional callback before each redelivery attempt.</param>
/// <param name="OnPrepareFailureCallback">Optional callback before handler/DLQ after retries exhausted.</param>
/// <param name="UseOriginalMessage">Restore original message (body + headers) before retries/handler.</param>
/// <param name="UseOriginalBody">Restore only original body before retries/handler.</param>
/// <param name="AllowRedeliveryWhileStopping">Allow retries during cancellation.</param>
/// <param name="LogStackTrace">Include stack trace in retry logs.</param>
/// <param name="LogExhausted">Log when retries are exhausted.</param>
public record OnExceptionHandler(
    Type ExceptionType,
    IReadOnlyList<RouteStep> HandlerSteps,
    int MaxRedeliveries,
    TimeSpan? RedeliveryDelay = null,
    double BackoffMultiplier = 1.0,
    bool UseExponentialBackoff = false,
    bool Handled = false,
    bool Continued = false,
    Func<Abstractions.IExchange, bool>? OnWhenPredicate = null,
    Microsoft.Extensions.Logging.LogLevel RetryAttemptedLogLevel = Microsoft.Extensions.Logging.LogLevel.Warning,
    Microsoft.Extensions.Logging.LogLevel RetriesExhaustedLogLevel = Microsoft.Extensions.Logging.LogLevel.Error,
    Action<Abstractions.IExchange>? OnExceptionOccurredCallback = null,
    Func<Abstractions.IExchange, bool>? RetryWhilePredicate = null,
    Action<Abstractions.IExchange>? OnRedeliveryCallback = null,
    Action<Abstractions.IExchange>? OnPrepareFailureCallback = null,
    bool UseOriginalMessage = false,
    bool UseOriginalBody = false,
    bool AllowRedeliveryWhileStopping = false,
    bool LogStackTrace = true,
    bool LogExhausted = true);

/// <summary>Step: set the exchange pattern.</summary>
/// <param name="Pattern">Exchange pattern to set.</param>
public record SetPatternStep(Abstractions.ExchangePattern Pattern) : RouteStep;

/// <summary>Step: create an Out response.</summary>
/// <param name="Factory">Factory to produce the response body.</param>
public record RespondStep(Func<Abstractions.IExchange, object?> Factory) : RouteStep;

/// <summary>Step: log a static message.</summary>
/// <param name="Message">Log message.</param>
/// <param name="Level">Log level (default: Information).</param>
public record LogStaticStep(string Message, LogLevel Level = LogLevel.Information) : RouteStep;

/// <summary>Step: log a dynamic message.</summary>
/// <param name="MessageFactory">Factory to produce the log message.</param>
/// <param name="Level">Log level (default: Information).</param>
public record LogDynamicStep(Func<Abstractions.IExchange, string> MessageFactory, LogLevel Level = LogLevel.Information) : RouteStep;

/// <summary>Step: rich structured log with multiple messages, headers and properties.</summary>
/// <param name="Messages">Static/template message strings.</param>
/// <param name="MessageFuncs">Dynamic message factory functions.</param>
/// <param name="HeaderNames">Header names to include in log output.</param>
/// <param name="PropertyNames">Exchange property names to include in log output.</param>
/// <param name="Level">Log level.</param>
/// <param name="ShowRouteId">Whether to include the route ID in the log.</param>
public record RichLogStep(
    IReadOnlyList<string> Messages,
    IReadOnlyList<Func<Abstractions.IExchange, string>> MessageFuncs,
    IReadOnlyList<string> HeaderNames,
    IReadOnlyList<string> PropertyNames,
    LogLevel Level = LogLevel.Information,
    bool ShowRouteId = false) : RouteStep;

/// <summary>Step: validate the exchange using an IMessageValidator instance.</summary>
/// <param name="Validator">Validator instance.</param>
/// <param name="ThrowOnFailure">Whether to throw on failure.</param>
public record ValidateInstanceStep(Validation.IMessageValidator Validator, bool ThrowOnFailure) : RouteStep;

/// <summary>Step: validate the exchange using a predicate function.</summary>
/// <param name="Predicate">Predicate returning true for valid.</param>
/// <param name="ErrorMessage">Error message on failure.</param>
/// <param name="ThrowOnFailure">Whether to throw on failure.</param>
public record ValidatePredicateStep(Func<Abstractions.IExchange, bool> Predicate, string ErrorMessage, bool ThrowOnFailure) : RouteStep;

/// <summary>Step: validate body against a JSON Schema string (forced JSON format).</summary>
/// <param name="SchemaJson">JSON Schema as a string.</param>
/// <param name="ThrowOnFailure">Whether to throw on failure.</param>
public record ValidateJsonSchemaStringStep(string SchemaJson, bool ThrowOnFailure) : RouteStep;

/// <summary>Step: validate body against a pre-parsed JsonSchema (forced JSON format).</summary>
/// <param name="Schema">Pre-parsed JsonSchema instance.</param>
/// <param name="ThrowOnFailure">Whether to throw on failure.</param>
public record ValidateJsonSchemaObjectStep(Json.Schema.JsonSchema Schema, bool ThrowOnFailure) : RouteStep;

/// <summary>Step: validate body against an XSD string (forced XML format).</summary>
/// <param name="XsdContent">XSD schema as a string.</param>
/// <param name="ThrowOnFailure">Whether to throw on failure.</param>
public record ValidateXsdStringStep(string XsdContent, bool ThrowOnFailure) : RouteStep;

/// <summary>Step: validate body against an XSD string with explicit target namespace (forced XML format).</summary>
/// <param name="TargetNamespace">Target namespace URI (or null).</param>
/// <param name="XsdContent">XSD schema as a string.</param>
/// <param name="ThrowOnFailure">Whether to throw on failure.</param>
public record ValidateXsdNamespaceStep(string? TargetNamespace, string XsdContent, bool ThrowOnFailure) : RouteStep;

/// <summary>Step: validate body against a pre-built XmlSchemaSet (forced XML format).</summary>
/// <param name="SchemaSet">A compiled XML Schema set.</param>
/// <param name="ThrowOnFailure">Whether to throw on failure.</param>
public record ValidateXsdSchemaSetStep(System.Xml.Schema.XmlSchemaSet SchemaSet, bool ThrowOnFailure) : RouteStep;

/// <summary>Step: serialize body to bytes.</summary>
/// <param name="SerializerType">Type implementing IMessageSerializer.</param>
public record MarshalStep(Type SerializerType) : RouteStep;

/// <summary>Step: deserialize body from bytes.</summary>
/// <param name="SerializerType">Type implementing IMessageSerializer.</param>
/// <param name="TargetType">Target type to deserialize to.</param>
public record UnmarshalStep(Type SerializerType, Type TargetType) : RouteStep;

/// <summary>Step: convert body to the specified type using ContentType for encoding.</summary>
/// <param name="TargetType">Target type to convert body to.</param>
public record ConvertBodyStep(Type TargetType) : RouteStep;

/// <summary>Step: wrap Stream body with a seekable StreamCache for re-reads.</summary>
/// <param name="SpoolThreshold">Optional spool threshold override (bytes).</param>
public record StreamCachingStep(long? SpoolThreshold = null) : RouteStep;

/// <summary>Step: streaming split using IAsyncEnumerable (no buffering).</summary>
/// <param name="Splitter">Function that returns an async enumerable of parts.</param>
/// <param name="SubSteps">Optional sub-route steps for each split part.</param>
/// <param name="StopOnException">Whether to stop on the first exception.</param>
public record StreamingSplitStep(
    Func<Abstractions.IExchange, IAsyncEnumerable<object?>> Splitter,
    IReadOnlyList<RouteStep>? SubSteps,
    bool StopOnException = true) : RouteStep;

/// <summary>Step: retry wrapper with policy parameters.</summary>
/// <param name="MaxRetries">Maximum number of retries.</param>
/// <param name="InitialDelay">Initial delay between retries (null = default 100ms).</param>
public record RetryStep(int MaxRetries, TimeSpan? InitialDelay) : RouteStep;

/// <summary>Step: dead-letter channel for failed exchanges.</summary>
/// <param name="DeadLetterUri">Endpoint URI for dead-lettered exchanges.</param>
public record DeadLetterChannelStep(string DeadLetterUri) : RouteStep;

/// <summary>Step: mark route as transacted with an optional transaction policy.</summary>
/// <param name="Policy">Transaction policy (null = default Required policy).</param>
public record TransactedStep(Transactions.TransactionPolicy? Policy = null) : RouteStep;

/// <summary>Step: idempotent consumer that deduplicates by key.</summary>
/// <param name="KeyExtractor">Function to extract the unique key from an exchange.</param>
/// <param name="Repository">Idempotent repository for tracking keys.</param>
/// <param name="SkipDuplicate">Whether to silently skip duplicates (true) or propagate with flag (false).</param>
/// <param name="SubSteps">Optional explicit body. See <see cref="FilterStep"/> for semantics.</param>
public record IdempotentConsumerStep(
    Func<Abstractions.IExchange, string> KeyExtractor,
    Abstractions.IIdempotentRepository Repository,
    bool SkipDuplicate = true,
    IReadOnlyList<RouteStep>? SubSteps = null) : RouteStep;

/// <summary>
/// Step: idempotent consumer that resolves its <see cref="Abstractions.IIdempotentRepository"/>
/// by logical name from <see cref="Abstractions.IIdempotentRepositoryProvider"/> at compile time.
/// Allows route definitions to be written before the repository instance exists.
/// </summary>
/// <param name="KeyExtractor">Function to extract the unique key from an exchange.</param>
/// <param name="RepositoryName">Logical repository name to resolve at compile time.</param>
/// <param name="SkipDuplicate">Whether to silently skip duplicates (true) or propagate with flag (false).</param>
/// <param name="SubSteps">Optional explicit body. See <see cref="FilterStep"/> for semantics.</param>
public record NamedIdempotentConsumerStep(
    Func<Abstractions.IExchange, string> KeyExtractor,
    string RepositoryName,
    bool SkipDuplicate = true,
    IReadOnlyList<RouteStep>? SubSteps = null) : RouteStep;

/// <summary>Step: remove a property from the exchange.</summary>
/// <param name="Key">Property key to remove.</param>
public record RemovePropertyStep(string Key) : RouteStep;

/// <summary>Step: remove (null out) the exchange body.</summary>
public record RemoveBodyStep : RouteStep;

/// <summary>Step: rethrow the current exchange exception (or throw InvalidOperationException if none).</summary>
public record RethrowExceptionStep : RouteStep;

/// <summary>Step: throw a new Exception with a message.</summary>
/// <param name="Message">Exception message.</param>
public record ThrowMessageStep(string Message) : RouteStep;

/// <summary>Step: throw an exception to halt processing.</summary>
/// <param name="Exception">The exception instance to throw.</param>
public record ThrowExceptionStep(Exception Exception) : RouteStep;

/// <summary>Step: construct and throw an exception by type and message.</summary>
/// <param name="ExceptionType">Exception type (must have a string constructor).</param>
/// <param name="Message">Exception message.</param>
public record ThrowExceptionTypeStep(Type ExceptionType, string Message) : RouteStep;

/// <summary>Step: stop the exchange.</summary>
public record StopStep : RouteStep;

/// <summary>Step: rollback all transacted actions on the exchange.</summary>
public record RollbackAllStep : RouteStep;

/// <summary>Step: begin an imperative transaction scope on the exchange.</summary>
/// <param name="Policy">Transaction policy (null = default Required policy).</param>
public record BeginTransactionStep(Transactions.TransactionPolicy? Policy = null) : RouteStep;

/// <summary>Step: commit the imperative transaction scope and all deferred actions.</summary>
public record CommitTransactionStep : RouteStep;

/// <summary>Step: rollback the imperative transaction scope and all deferred actions.</summary>
public record RollbackTransactionStep : RouteStep;

/// <summary>Step: mark the current exception as handled (not re-thrown).</summary>
public record ExceptionHandledStep : RouteStep;

// ── Expression / Predicate-based steps ──

/// <summary>Step: filter by <see cref="IPredicate"/> instance.</summary>
/// <param name="Predicate">Predicate to evaluate.</param>
/// <param name="SubSteps">Optional explicit body. See <see cref="FilterStep"/> for semantics.</param>
public record FilterPredicateStep(
    IPredicate Predicate,
    IReadOnlyList<RouteStep>? SubSteps = null) : RouteStep;

/// <summary>Step: filter by string expression evaluated as boolean (e.g. <c>"${header.active}"</c>).</summary>
/// <param name="Expression">Expression string evaluated as boolean.</param>
/// <param name="SubSteps">Optional explicit body. See <see cref="FilterStep"/> for semantics.</param>
public record FilterExpressionStep(
    string Expression,
    IReadOnlyList<RouteStep>? SubSteps = null) : RouteStep;

/// <summary>Step: set body via <see cref="IExpression"/> instance.</summary>
/// <param name="Expression">Expression producing the new body value.</param>
public record SetBodyExpressionStep(IExpression Expression) : RouteStep;

/// <summary>Step: set body via string expression template (e.g. <c>"${header.greeting} ${header.name}"</c>).</summary>
/// <param name="Expression">Expression template string.</param>
public record SetBodyStringExpressionStep(string Expression) : RouteStep;

/// <summary>Step: set header via <see cref="IExpression"/> instance.</summary>
/// <param name="Name">Header name.</param>
/// <param name="Expression">Expression producing the header value.</param>
public record SetHeaderExpressionStep(string Name, IExpression Expression) : RouteStep;

/// <summary>Step: set header via string expression template.</summary>
/// <param name="Name">Header name.</param>
/// <param name="Expression">Expression template string.</param>
public record SetHeaderStringExpressionStep(string Name, string Expression) : RouteStep;

/// <summary>Step: transform body via <see cref="IExpression"/> instance.</summary>
/// <param name="Expression">Expression producing the transformed body.</param>
public record TransformExpressionStep(IExpression Expression) : RouteStep;

/// <summary>Step: transform body via string expression template.</summary>
/// <param name="Expression">Expression template string.</param>
public record TransformStringExpressionStep(string Expression) : RouteStep;

/// <summary>Step: split using <see cref="IExpression"/> to produce an iterable.</summary>
/// <param name="Expression">Expression returning a collection.</param>
/// <param name="SubSteps">Optional sub-route steps for each split part.</param>
/// <param name="ParallelProcessing">Whether to process parts in parallel.</param>
/// <param name="MaxDegreeOfParallelism">Max concurrent tasks (0 = processor count).</param>
/// <param name="AggregationStrategy">
/// Optional pair-wise aggregation strategy (Camel-compatible): for the first part
/// <c>oldExchange == null</c> so the strategy can perform a seed/wrap.
/// </param>
/// <param name="StopOnException">Whether to stop on first exception.</param>
/// <param name="Timeout">Timeout for the operation.</param>
/// <param name="ParallelAggregate">When true and ParallelProcessing is true, aggregation runs in-task under a lock (strategy must be thread-safe).</param>
/// <param name="AggregateOnException">When true, failed split exchanges still feed into the aggregation strategy.</param>
public record SplitExpressionStep(
    IExpression Expression,
    IReadOnlyList<RouteStep>? SubSteps,
    bool ParallelProcessing = false,
    int MaxDegreeOfParallelism = 0,
    Func<IExchange?, IExchange, IExchange>? AggregationStrategy = null,
    bool StopOnException = true,
    TimeSpan Timeout = default,
    bool ParallelAggregate = false,
    bool AggregateOnException = false) : RouteStep;

/// <summary>Step: log using ExpressionResolver template syntax (<c>${body}</c>, <c>${header.x}</c> placeholders).</summary>
/// <param name="Template">Template string with <c>${...}</c> placeholders.</param>
/// <param name="Level">Log level.</param>
public record LogTemplateStep(string Template, LogLevel Level = LogLevel.Information) : RouteStep;

/// <summary>A Choice/When clause using an <see cref="IPredicate"/> instance.</summary>
/// <param name="Predicate">Predicate to evaluate.</param>
/// <param name="Steps">Steps to execute when predicate matches.</param>
public record ChoiceWhenPredicateClause(IPredicate Predicate, IReadOnlyList<RouteStep> Steps);

/// <summary>A Choice/When clause using a string expression evaluated as boolean.</summary>
/// <param name="Expression">Expression string.</param>
/// <param name="Steps">Steps to execute when expression is true.</param>
public record ChoiceWhenExpressionClause(string Expression, IReadOnlyList<RouteStep> Steps);

// ── EIP: Sampling ──

/// <summary>Step: count-based message sampler — passes every Nth message.</summary>
/// <param name="MessageFrequency">Pass every Nth message (1 = all).</param>
public record SampleCountStep(long MessageFrequency) : RouteStep;

/// <summary>Step: time-based message sampler — passes at most one message per period.</summary>
/// <param name="Period">Minimum interval between sampled messages.</param>
public record SamplePeriodStep(TimeSpan Period) : RouteStep;

// ── EIP: Throttle / CircuitBreaker / Resequencer / RecipientList / Enrich / DynamicRouter ──

/// <summary>Step: throttle exchange rate.</summary>
/// <param name="MaxPerPeriod">Maximum number of exchanges per period.</param>
/// <param name="Period">Time period (null = 1 second).</param>
public record ThrottleStep(int MaxPerPeriod, TimeSpan? Period = null) : RouteStep;

/// <summary>Step: throttle exchange rate where max-per-period is resolved from a string expression.</summary>
/// <param name="Expression">Expression string resolving to an integer (max per period).</param>
/// <param name="Period">Time period (null = 1 second).</param>
public record ThrottleExpressionStep(string Expression, TimeSpan? Period = null) : RouteStep;

/// <summary>Step: per-key throttle — each key gets an independent rate limiter.</summary>
/// <param name="KeyExtractor">Function extracting the throttle key from the exchange.</param>
/// <param name="MaxPerPeriod">Maximum exchanges per period per key.</param>
/// <param name="Period">Time period (null = 1 second).</param>
public record KeyedThrottleStep(
    Func<Abstractions.IExchange, string> KeyExtractor,
    int MaxPerPeriod,
    TimeSpan? Period = null) : RouteStep;

/// <summary>Step: per-key debounce — suppresses rapid-fire messages, forwarding only the last one after a quiet period.</summary>
/// <param name="KeyExtractor">Function extracting the debounce key from the exchange.</param>
/// <param name="QuietPeriod">Duration of silence required before forwarding.</param>
public record DebounceStep(
    Func<Abstractions.IExchange, string> KeyExtractor,
    TimeSpan QuietPeriod) : RouteStep;

/// <summary>Step: circuit breaker wrapper.</summary>
/// <param name="FailureThreshold">Consecutive failures before opening.</param>
/// <param name="ResetTimeout">Time before probing recovery.</param>
/// <param name="HalfOpenMaxCalls">Max probing calls in HalfOpen state.</param>
/// <param name="FallbackSteps">Optional fallback sub-route when circuit is open.</param>
public record CircuitBreakerStep(
    int FailureThreshold,
    TimeSpan? ResetTimeout = null,
    int HalfOpenMaxCalls = 1,
    IReadOnlyList<RouteStep>? FallbackSteps = null) : RouteStep;

/// <summary>Step: resequence exchanges by key.</summary>
/// <param name="KeySelector">Function extracting sequence key (lower = earlier).</param>
/// <param name="BatchSize">Max batch size before flush.</param>
/// <param name="Timeout">Max batch timeout.</param>
public record ResequenceStep(
    Func<Abstractions.IExchange, long> KeySelector,
    int BatchSize = 100,
    TimeSpan? Timeout = null) : RouteStep;

/// <summary>Step: dynamic recipient list (fan-out with runtime URIs).</summary>
/// <param name="RecipientListFactory">Function returning target URIs from the exchange.</param>
/// <param name="ParallelProcessing">Whether to send in parallel.</param>
/// <param name="StopOnException">Whether to stop on the first exception.</param>
/// <param name="AggregationStrategy">Optional pair-wise aggregation.</param>
public record RecipientListStep(
    Func<Abstractions.IExchange, IEnumerable<string>> RecipientListFactory,
    bool ParallelProcessing = false,
    bool StopOnException = false,
    Func<Abstractions.IExchange, Abstractions.IExchange, Abstractions.IExchange>? AggregationStrategy = null) : RouteStep;

/// <summary>Step: content enricher — calls an endpoint and merges response.</summary>
/// <param name="ResourceUri">URI of the enrichment endpoint.</param>
/// <param name="MergeStrategy">Merge function: (original, enriched) → merged.</param>
public record EnrichStep(
    string ResourceUri,
    Func<Abstractions.IExchange, Abstractions.IExchange, Abstractions.IExchange> MergeStrategy) : RouteStep;

/// <summary>Step: poll enricher — polls an endpoint and merges response (nullable polled).</summary>
/// <param name="ResourceUri">URI of the polling endpoint.</param>
/// <param name="MergeStrategy">Merge function: (original, polled?) → merged.</param>
/// <param name="Timeout">Timeout for polling.</param>
public record PollEnrichStep(
    string ResourceUri,
    Func<Abstractions.IExchange, Abstractions.IExchange?, Abstractions.IExchange> MergeStrategy,
    TimeSpan? Timeout = null) : RouteStep;

/// <summary>Step: dynamic router — iterative routing until null.</summary>
/// <param name="RoutingFunction">Function returning next URI or null to stop.</param>
public record DynamicRouterStep(
    Func<Abstractions.IExchange, string?> RoutingFunction) : RouteStep;

/// <summary>Step: wraps sub-steps in a named Activity span for per-step telemetry.</summary>
/// <param name="SpanName">Activity span name (may contain ${...} expression templates).</param>
/// <param name="SubSteps">Steps to execute inside the span.</param>
public record TracedStep(
    string SpanName,
    IReadOnlyList<RouteStep> SubSteps) : RouteStep;

/// <summary>Step: wraps sub-steps with per-step metrics collection (counter, histogram).</summary>
/// <param name="StepName">Static metric step name (must NOT contain ${...} expressions — cardinality protection).</param>
/// <param name="SubSteps">Steps to execute inside the metered scope.</param>
public record MeteredStep(
    string StepName,
    IReadOnlyList<RouteStep> SubSteps) : RouteStep;

// ── Load Balancer ───────────────────────────────────────────

/// <summary>Step: load balance across multiple endpoints using a pluggable strategy.</summary>
/// <param name="Strategy">The load balancer strategy (RoundRobin, Random, Failover, Weighted, Sticky, etc.).</param>
/// <param name="Endpoints">Target endpoint URIs to balance across.</param>
public record LoadBalanceStep(
    Abstractions.ILoadBalancerStrategy Strategy,
    IReadOnlyList<string> Endpoints) : RouteStep;

// ── Scatter-Gather ──────────────────────────────────────────

/// <summary>Step: scatter-gather across multiple endpoints with mandatory aggregation.</summary>
/// <param name="StaticRecipients">Fixed recipient URIs (null if dynamic).</param>
/// <param name="DynamicRecipients">Factory that resolves URIs at runtime (null if static).</param>
/// <param name="AggregationStrategy">Mandatory pair-wise aggregation: (accumulated, current) → merged.</param>
/// <param name="ParallelProcessing">Whether to scatter in parallel.</param>
/// <param name="MaxDegreeOfParallelism">Max concurrent sends (0 = processor count).</param>
/// <param name="StopOnException">Whether to stop on the first error.</param>
/// <param name="Timeout">Timeout for the entire operation (TimeSpan.Zero = no timeout).</param>
public record ScatterGatherStep(
    string[]? StaticRecipients,
    Func<Abstractions.IExchange, IEnumerable<string>>? DynamicRecipients,
    Func<Abstractions.IExchange, Abstractions.IExchange, Abstractions.IExchange> AggregationStrategy,
    bool ParallelProcessing = true,
    int MaxDegreeOfParallelism = 0,
    bool StopOnException = false,
    TimeSpan Timeout = default) : RouteStep;

// ── Claim Check ─────────────────────────────────────────────

/// <summary>Step: claim check operation (store/retrieve body via external repository).</summary>
/// <param name="Repository">The claim check repository instance.</param>
/// <param name="Operation">The claim check operation to perform.</param>
/// <param name="Key">Explicit key for Set/Get/GetAndRemove. Null for Push/Pop or auto-key.</param>
/// <param name="Ttl">Optional TTL for stored data (Set/Push only).</param>
public record ClaimCheckStep(
    Abstractions.IClaimCheckRepository Repository,
    Abstractions.ClaimCheckOperation Operation,
    string? Key = null,
    TimeSpan? Ttl = null) : RouteStep;

// ── Bean / Service Activator ────────────────────────────────

/// <summary>Step: resolve a DI service and invoke a method on it.</summary>
/// <param name="ServiceType">The type of service to resolve from the exchange's ServiceProvider.</param>
/// <param name="Method">Normalized delegate: (service, exchange, ct) → Task.</param>
public record BeanStep(
    Type ServiceType,
    Func<object, Abstractions.IExchange, CancellationToken, Task> Method) : RouteStep;

// ── Saga ────────────────────────────────────────────────────

/// <summary>Step: orchestrated saga with forward actions and reverse compensations on failure.</summary>
/// <param name="Steps">Ordered saga step entries (action + optional compensate).</param>
/// <param name="OnCompletion">Optional callback invoked after all steps succeed.</param>
public record SagaRouteStep(
    SagaStepEntry[] Steps,
    Func<Abstractions.IExchange, CancellationToken, Task>? OnCompletion) : RouteStep;

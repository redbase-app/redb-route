using Microsoft.Extensions.Logging;

namespace redb.Route.Abstractions;

/// <summary>
/// Fluent API for defining a message route (untyped).
/// Records steps that are later compiled into a processor chain by RouteCompiler.
/// Each method returns the definition for fluent chaining.
/// </summary>
public interface IRouteDefinition
{
    // ── Identity ──

    /// <summary>Assigns a unique route identifier.</summary>
    /// <param name="routeId">Unique route ID.</param>
    /// <returns>This definition for chaining.</returns>
    IRouteDefinition RouteId(string routeId);

    /// <summary>Gets the assigned route ID (null if not set).</summary>
    string? GetRouteId();

    /// <summary>
    /// Sets whether this route should auto-start when the context starts.
    /// Default is true (backward compatible).
    /// Routes with AutoStart=false remain stopped until explicitly started via StartRoute().
    /// </summary>
    /// <param name="value">True to auto-start, false to remain stopped.</param>
    /// <returns>This definition for chaining.</returns>
    IRouteDefinition AutoStart(bool value = true);

    /// <summary>Gets whether this route should auto-start. Default is true.</summary>
    bool GetAutoStart();

    /// <summary>
    /// Sets the per-exchange processing timeout for this route.
    /// If not set, falls back to <see cref="Configuration.RouteEngineOptions.DefaultProcessingTimeout"/>.
    /// </summary>
    IRouteDefinition ProcessingTimeout(TimeSpan timeout);

    // ── Route Policy ──

    /// <summary>Assigns a <see cref="IRoutePolicy"/> to this route for lifecycle control.</summary>
    /// <param name="policy">Route policy instance.</param>
    /// <returns>This definition for chaining.</returns>
    IRouteDefinition RoutePolicy(IRoutePolicy policy);

    /// <summary>Gets the assigned route policy, or <c>null</c> if not set.</summary>
    IRoutePolicy? GetRoutePolicy();

    /// <summary>
    /// Marks this route as a cluster singleton. When <c>true</c> and an
    /// <see cref="IRoutePolicyFactory"/> is registered, the factory will create
    /// a <see cref="IRoutePolicy"/> that ensures only one node runs this route.
    /// </summary>
    /// <param name="value">True to enable cluster singleton mode.</param>
    /// <returns>This definition for chaining.</returns>
    IRouteDefinition Cluster(bool value = true);

    /// <summary>Gets whether this route is marked as a cluster singleton.</summary>
    bool GetCluster();

    // ── Context ──

    /// <summary>Gets the route context associated with this definition (available after the builder is configured).</summary>
    IRouteContext? GetContext();

    // ── Source ──

    /// <summary>Sets the source (consumer) endpoint URI.</summary>
    /// <param name="uri">Source endpoint URI (e.g., "kafka://orders").</param>
    /// <returns>This definition for chaining.</returns>
    IRouteDefinition From(string uri);

    /// <summary>Gets the source endpoint URI.</summary>
    string? GetFromUri();

    // ── Destination ──

    /// <summary>Sends the exchange to an endpoint via producer.</summary>
    /// <param name="uri">Target endpoint URI.</param>
    /// <returns>This definition for chaining.</returns>
    IRouteDefinition To(string uri);

    // ── Transform / Enrich ──

    /// <summary>Sets the body of the In message to a static value.</summary>
    /// <param name="value">New body value.</param>
    /// <returns>This definition for chaining.</returns>
    IRouteDefinition SetBody(object? value);

    /// <summary>Sets the body using a factory function from the exchange.</summary>
    /// <param name="factory">Factory that produces the new body.</param>
    /// <returns>This definition for chaining.</returns>
    IRouteDefinition SetBody(Func<IExchange, object?> factory);

    /// <summary>Sets the message body using an <see cref="IExpression"/> instance.</summary>
    /// <param name="expression">Expression producing the new body value.</param>
    /// <returns>This definition for chaining.</returns>
    IRouteDefinition SetBody(IExpression expression);

    /// <summary>Sets the message body using a string expression template.</summary>
    /// <param name="expression">Expression template (e.g. <c>"${header.greeting} ${header.name}"</c>).</param>
    /// <returns>This definition for chaining.</returns>
    [Obsolete("Use .SetBody(Expr(\"${...}\")) instead. Will be removed in v2.0.")]
    IRouteDefinition SetBodyExpression(string expression);

    /// <summary>Transforms the In message body using a mapping function.</summary>
    /// <param name="transform">Function to transform the body.</param>
    /// <returns>This definition for chaining.</returns>
    IRouteDefinition Transform(Func<IExchange, object?> transform);

    /// <summary>Transforms the message body using an <see cref="IExpression"/> instance.</summary>
    /// <param name="expression">Expression producing the transformed body.</param>
    /// <returns>This definition for chaining.</returns>
    IRouteDefinition Transform(IExpression expression);

    /// <summary>Transforms the message body using a string expression template.</summary>
    /// <param name="expression">Expression template string.</param>
    /// <returns>This definition for chaining.</returns>
    [Obsolete("Use .Transform(Expr(\"${...}\")) instead. Will be removed in v2.0.")]
    IRouteDefinition TransformExpression(string expression);

    /// <summary>Sets a header on the In message to a static value.</summary>
    /// <param name="key">Header key.</param>
    /// <param name="value">Header value.</param>
    /// <returns>This definition for chaining.</returns>
    IRouteDefinition SetHeader(string key, object? value);

    /// <summary>Sets a header on the In message using a factory function.</summary>
    /// <param name="key">Header key.</param>
    /// <param name="factory">Factory that produces the header value from the exchange.</param>
    /// <returns>This definition for chaining.</returns>
    IRouteDefinition SetHeader(string key, Func<IExchange, object?> factory);

    /// <summary>Sets a header using an <see cref="IExpression"/> instance.</summary>
    /// <param name="key">Header key.</param>
    /// <param name="expression">Expression producing the header value.</param>
    /// <returns>This definition for chaining.</returns>
    IRouteDefinition SetHeader(string key, IExpression expression);

    /// <summary>Sets a header using a string expression template.</summary>
    /// <param name="key">Header key.</param>
    /// <param name="expression">Expression template string.</param>
    /// <returns>This definition for chaining.</returns>
    [Obsolete("Use .SetHeader(key, Expr(\"${...}\")) instead. Will be removed in v2.0.")]
    IRouteDefinition SetHeaderExpression(string key, string expression);

    /// <summary>Removes a header from the In message.</summary>
    /// <param name="key">Header key to remove.</param>
    /// <returns>This definition for chaining.</returns>
    IRouteDefinition RemoveHeader(string key);

    /// <summary>Sets a property on the exchange.</summary>
    /// <param name="key">Property key.</param>
    /// <param name="value">Property value.</param>
    /// <returns>This definition for chaining.</returns>
    IRouteDefinition SetProperty(string key, object? value);

    /// <summary>Sets a property on the exchange using a factory function.</summary>
    /// <param name="key">Property key.</param>
    /// <param name="factory">Factory that produces the property value.</param>
    /// <returns>This definition for chaining.</returns>
    IRouteDefinition SetProperty(string key, Func<IExchange, object?> factory);

    /// <summary>Sets a property on the exchange using an <see cref="IExpression"/> instance.</summary>
    /// <param name="key">Property key.</param>
    /// <param name="expression">Expression producing the property value.</param>
    /// <returns>This definition for chaining.</returns>
    IRouteDefinition SetProperty(string key, IExpression expression);

    /// <summary>Sets a property on the exchange using a string expression template (e.g. <c>"${header.userId}"</c>).</summary>
    /// <param name="key">Property key.</param>
    /// <param name="expression">Expression template with <c>${...}</c> placeholders.</param>
    /// <returns>This definition for chaining.</returns>
    [Obsolete("Use .SetProperty(key, Expr(\"${...}\")) instead. Will be removed in v2.0.")]
    IRouteDefinition SetPropertyExpression(string key, string expression);

    /// <summary>Removes a property from the exchange.</summary>
    /// <param name="key">Property key to remove.</param>
    /// <returns>This definition for chaining.</returns>
    IRouteDefinition RemoveProperty(string key);

    /// <summary>Removes (nulls out) the exchange body.</summary>
    /// <returns>This definition for chaining.</returns>
    IRouteDefinition RemoveBody();

    /// <summary>
    /// Rethrows the current exchange exception. If no exception is set, throws <see cref="InvalidOperationException"/>.
    /// Useful in catch/doFinally blocks to rethrow the original error.
    /// </summary>
    /// <returns>This definition for chaining.</returns>
    IRouteDefinition ThrowException();

    /// <summary>Throws a new <see cref="Exception"/> with the specified message.</summary>
    /// <param name="message">Exception message.</param>
    /// <returns>This definition for chaining.</returns>
    IRouteDefinition ThrowException(string message);

    /// <summary>Throws the specified exception instance, halting processing.</summary>
    /// <param name="exception">Exception to throw.</param>
    /// <returns>This definition for chaining.</returns>
    IRouteDefinition ThrowException(Exception exception);

    /// <summary>Constructs and throws an exception by type and message.</summary>
    /// <param name="exceptionType">Exception type (must have a string constructor).</param>
    /// <param name="message">Exception message.</param>
    /// <returns>This definition for chaining.</returns>
    IRouteDefinition ThrowException(Type exceptionType, string message);

    /// <summary>
    /// Constructs and throws a typed exception. If <paramref name="message"/> is null,
    /// uses the parameterless constructor.
    /// </summary>
    /// <typeparam name="TException">Exception type.</typeparam>
    /// <param name="message">Optional exception message.</param>
    /// <returns>This definition for chaining.</returns>
    IRouteDefinition ThrowException<TException>(string? message = null) where TException : Exception, new();

    // ── Filtering ──

    /// <summary>Filters exchanges by predicate. Non-matching exchanges are skipped.</summary>
    /// <param name="predicate">Filter condition.</param>
    /// <returns>This definition for chaining.</returns>
    IRouteDefinition Filter(Func<IExchange, bool> predicate);

    /// <summary>Filters exchanges using an <see cref="IPredicate"/> instance.</summary>
    /// <param name="predicate">Predicate to evaluate.</param>
    /// <returns>This definition for chaining.</returns>
    IRouteDefinition Filter(IPredicate predicate);

    /// <summary>
    /// Filters exchanges using a string expression evaluated as boolean.
    /// Supports <c>${...}</c> placeholders (e.g. <c>"${header.active}"</c>, <c>"${header.myInt > 0}"</c>).
    /// </summary>
    /// <param name="expression">Expression string.</param>
    /// <returns>This definition for chaining.</returns>
    IRouteDefinition Filter(string expression);

    // ── Processing ──

    /// <summary>Processes the exchange with an async delegate.</summary>
    /// <param name="processor">Async processing delegate.</param>
    /// <returns>This definition for chaining.</returns>
    IRouteDefinition Process(Func<IExchange, CancellationToken, Task> processor);

    /// <summary>Processes the exchange with a synchronous delegate.</summary>
    /// <param name="action">Synchronous processing action.</param>
    /// <returns>This definition for chaining.</returns>
    IRouteDefinition Process(Action<IExchange> action);

    /// <summary>Processes the exchange with an IProcessor instance.</summary>
    /// <param name="processor">Processor instance.</param>
    /// <returns>This definition for chaining.</returns>
    IRouteDefinition Process(IProcessor processor);

    // ── Content-Based Routing ──

    /// <summary>
    /// Starts a content-based router (choice). Evaluates when-clauses in order,
    /// executes first matching branch.
    /// </summary>
    /// <param name="configure">Action to configure when/otherwise clauses.</param>
    /// <returns>This definition for chaining.</returns>
    IRouteDefinition Choice(Action<IChoiceDefinition> configure);

    // ── Multicast / WireTap ──

    /// <summary>Sends the exchange to multiple endpoints in parallel (fan-out).</summary>
    /// <param name="uris">Target endpoint URIs.</param>
    /// <returns>This definition for chaining.</returns>
    IRouteDefinition Multicast(params string[] uris);

    /// <summary>Sends the exchange to multiple endpoints with full control over execution.</summary>
    /// <param name="uris">Target endpoint URIs.</param>
    /// <param name="parallelProcessing">Whether to process targets in parallel.</param>
    /// <param name="aggregationStrategy">Optional pair-wise aggregation: (aggregated, current) → merged.</param>
    /// <param name="stopOnException">Whether to stop on the first exception.</param>
    /// <param name="timeout">Timeout for the operation (default = no timeout).</param>
    /// <param name="maxDegreeOfParallelism">Max concurrent tasks (0 = processor count).</param>
    /// <returns>This definition for chaining.</returns>
    IRouteDefinition Multicast(
        string[] uris,
        bool parallelProcessing,
        Func<IExchange, IExchange, IExchange>? aggregationStrategy = null,
        bool stopOnException = false,
        TimeSpan timeout = default,
        int maxDegreeOfParallelism = 0);

    /// <summary>Sends a fire-and-forget copy of the exchange to a tap endpoint.</summary>
    /// <param name="uri">Tap endpoint URI.</param>
    /// <param name="onPrepare">Optional callback to modify the cloned exchange before tapping.</param>
    /// <param name="newBodyFactory">Optional factory to replace the body on the tapped clone.</param>
    /// <returns>This definition for chaining.</returns>
    IRouteDefinition WireTap(string uri, Action<IExchange>? onPrepare = null, Func<IExchange, object?>? newBodyFactory = null);

    // ── Split / Aggregate ──

    /// <summary>Splits the exchange body into multiple exchanges and processes each.</summary>
    /// <param name="splitter">Function that splits the body into parts.</param>
    /// <param name="configure">Optional sub-route definition for each split part.</param>
    /// <param name="parallelProcessing">Whether to process parts in parallel.</param>
    /// <param name="maxDegreeOfParallelism">Max concurrent tasks when parallel (0 = processor count).</param>
    /// <param name="aggregationStrategy">Optional pair-wise aggregation: (aggregated, current) → merged.</param>
    /// <param name="stopOnException">Whether to stop on the first exception.</param>
    /// <param name="timeout">Timeout for the operation (default = no timeout).</param>
    /// <returns>This definition for chaining.</returns>
    IRouteDefinition Split(
        Func<IExchange, IEnumerable<object?>> splitter,
        Action<IRouteDefinition>? configure = null,
        bool parallelProcessing = false,
        int maxDegreeOfParallelism = 0,
        Func<IExchange, IExchange, IExchange>? aggregationStrategy = null,
        bool stopOnException = true,
        TimeSpan timeout = default);

    /// <summary>Splits the exchange using an <see cref="IExpression"/> to produce an iterable.</summary>
    /// <param name="expression">Expression returning a collection.</param>
    /// <param name="configure">Optional sub-route definition for each split part.</param>
    /// <param name="parallelProcessing">Whether to process parts in parallel.</param>
    /// <param name="maxDegreeOfParallelism">Max concurrent tasks when parallel (0 = processor count).</param>
    /// <param name="aggregationStrategy">Optional pair-wise aggregation.</param>
    /// <param name="stopOnException">Whether to stop on the first exception.</param>
    /// <param name="timeout">Timeout for the operation.</param>
    /// <returns>This definition for chaining.</returns>
    IRouteDefinition Split(
        IExpression expression,
        Action<IRouteDefinition>? configure = null,
        bool parallelProcessing = false,
        int maxDegreeOfParallelism = 0,
        Func<IExchange, IExchange, IExchange>? aggregationStrategy = null,
        bool stopOnException = true,
        TimeSpan timeout = default);

    /// <summary>Splits body using an async streaming function (no buffering). Each element processed one at a time.</summary>
    /// <param name="splitter">Function returning an IAsyncEnumerable of parts.</param>
    /// <param name="configure">Optional sub-route configuration for each part.</param>
    /// <param name="stopOnException">Whether to stop on the first exception.</param>
    /// <returns>This definition for chaining.</returns>
    IRouteDefinition Split(
        Func<IExchange, IAsyncEnumerable<object?>> splitter,
        Action<IRouteDefinition>? configure = null,
        bool stopOnException = true);

    /// <summary>Aggregates multiple exchanges into one using correlation and strategy.</summary>
    /// <param name="correlationKey">Function to extract the correlation key.</param>
    /// <param name="aggregationStrategy">Function to merge exchanges.</param>
    /// <param name="completionPredicate">Predicate for when aggregation is complete.</param>
    /// <returns>This definition for chaining.</returns>
    IRouteDefinition Aggregate(
        Func<IExchange, string> correlationKey,
        Func<IExchange, IExchange, IExchange> aggregationStrategy,
        Func<IExchange, bool> completionPredicate);

    // ── Loop / Delay ──

    /// <summary>Repeats the body processor a fixed number of times.</summary>
    /// <param name="count">Number of iterations.</param>
    /// <param name="configure">Sub-route definition for the loop body.</param>
    /// <param name="copy">If true, each iteration receives a clone of the original exchange (Camel-style loop().copy()).</param>
    /// <param name="shareScope">If true (default), copy-mode iterations share the parent's DI scope (same DB connection, same TX).</param>
    /// <returns>This definition for chaining.</returns>
    IRouteDefinition Loop(int count, Action<IRouteDefinition> configure, bool copy = false, bool shareScope = true);

    /// <summary>Repeats while the predicate is true.</summary>
    /// <param name="condition">Predicate evaluated before each iteration.</param>
    /// <param name="configure">Sub-route definition for the loop body.</param>
    /// <param name="copy">If true, each iteration receives a clone of the original exchange (Camel-style loop().copy()).</param>
    /// <param name="shareScope">If true (default), copy-mode iterations share the parent's DI scope (same DB connection, same TX).</param>
    /// <returns>This definition for chaining.</returns>
    IRouteDefinition Loop(Func<IExchange, bool> condition, Action<IRouteDefinition> configure, bool copy = false, bool shareScope = true);

    /// <summary>Repeats the specified number of times, where count is resolved from a string expression (e.g. <c>"${header.loopCount}"</c>).</summary>
    /// <param name="expression">Expression string resolving to an integer count.</param>
    /// <param name="configure">Sub-route definition for the loop body.</param>
    /// <param name="copy">If true, each iteration receives a clone of the original exchange.</param>
    /// <param name="shareScope">If true (default), copy-mode iterations share the parent's DI scope (same DB connection, same TX).</param>
    /// <returns>This definition for chaining.</returns>
    IRouteDefinition LoopExpression(string expression, Action<IRouteDefinition> configure, bool copy = false, bool shareScope = true);

    /// <summary>Delays processing by the specified duration.</summary>
    /// <param name="delay">Delay duration.</param>
    /// <returns>This definition for chaining.</returns>
    IRouteDefinition Delay(TimeSpan delay);

    /// <summary>Delays processing by a dynamic duration computed from the exchange.</summary>
    /// <param name="factory">Function that computes the delay from the exchange.</param>
    /// <returns>This definition for chaining.</returns>
    IRouteDefinition Delay(Func<IExchange, TimeSpan> factory);

    /// <summary>Delays processing by a duration resolved from a string expression (e.g. <c>"${header.delay}"</c>).
    /// The expression must resolve to a <see cref="TimeSpan"/>, a number (interpreted as milliseconds), or a parseable string.</summary>
    /// <param name="expression">Expression string with <c>${...}</c> placeholders.</param>
    /// <returns>This definition for chaining.</returns>
    IRouteDefinition DelayExpression(string expression);

    // ── Error Handling ──

    /// <summary>
    /// Wraps the subsequent steps in try-catch error handling.
    /// </summary>
    /// <param name="configure">Action to configure catch/finally clauses.</param>
    /// <returns>This definition for chaining.</returns>
    IRouteDefinition TryCatch(Action<ITryCatchDefinition> configure);

    /// <summary>
    /// Registers a global exception handler for this route.
    /// </summary>
    /// <param name="configure">Action to configure exception handlers.</param>
    /// <returns>This definition for chaining.</returns>
    IRouteDefinition OnException(Action<IOnExceptionDefinition> configure);

    // ── Exchange Pattern / Response ──

    /// <summary>Sets the exchange pattern explicitly.</summary>
    /// <param name="pattern">Exchange pattern to set.</param>
    /// <returns>This definition for chaining.</returns>
    IRouteDefinition SetPattern(ExchangePattern pattern);

    /// <summary>Creates an Out message response (for InOut exchanges).</summary>
    /// <param name="factory">Factory that produces the response body from the exchange.</param>
    /// <returns>This definition for chaining.</returns>
    IRouteDefinition Respond(Func<IExchange, object?> factory);

    // ── Logging ──

    /// <summary>
    /// Logs a message for each exchange. Supports plain strings and
    /// <c>${...}</c> template placeholders (e.g. <c>"Processing ${body} with id ${header.id}"</c>)
    /// which are resolved automatically via ExpressionResolver.
    /// </summary>
    /// <param name="message">Log message (plain or with <c>${...}</c> placeholders).</param>
    /// <param name="level">Log level (default: Information).</param>
    /// <returns>This definition for chaining.</returns>
    IRouteDefinition Log(string message, LogLevel level = LogLevel.Information);

    /// <summary>Logs a dynamic message computed from the exchange.</summary>
    /// <param name="messageFactory">Factory to compute the log message.</param>
    /// <param name="level">Log level (default: Information).</param>
    /// <returns>This definition for chaining.</returns>
    IRouteDefinition Log(Func<IExchange, string> messageFactory, LogLevel level = LogLevel.Information);

    /// <summary>
    /// Opens a rich log scope. Use <c>.Message()</c>, <c>.Header()</c>, <c>.Property()</c>
    /// to accumulate log directives, then <c>.EndLog()</c> or <c>.End()</c> to close.
    /// </summary>
    /// <param name="level">Log level (default: Information).</param>
    /// <returns>Log scope for fluent chaining.</returns>
    IRouteDefinition Log(LogLevel level);

    /// <summary>Adds a static/template message to the current Log scope.</summary>
    /// <param name="message">Static or template (<c>${...}</c>) string.</param>
    /// <returns>Log scope for chaining.</returns>
    IRouteDefinition Message(string message);

    /// <summary>Adds a dynamic message factory to the current Log scope.</summary>
    /// <param name="messageFunc">Function producing the message from the exchange.</param>
    /// <returns>Log scope for chaining.</returns>
    IRouteDefinition Message(Func<IExchange, string> messageFunc);

    /// <summary>Adds a header name to include in the log output.</summary>
    /// <param name="name">Header name.</param>
    /// <returns>Log scope for chaining.</returns>
    IRouteDefinition Header(string name);

    /// <summary>Adds an exchange property name to include in the log output.</summary>
    /// <param name="name">Property name.</param>
    /// <returns>Log scope for chaining.</returns>
    IRouteDefinition Property(string name);

    /// <summary>Includes the route ID in the log output.</summary>
    /// <returns>Log scope for chaining.</returns>
    IRouteDefinition ShowRouteId();

    /// <summary>Ends a Log scope and returns to the parent definition.</summary>
    /// <returns>Parent definition for chaining.</returns>
    IRouteDefinition EndLog();

    // ── Validation ──

    /// <summary>
    /// Validates the exchange using the provided <see cref="Validation.IMessageValidator"/> instance.
    /// On failure, sets <c>ValidationErrors</c> and <c>ValidationResult</c> properties.
    /// </summary>
    /// <param name="validator">Validator instance (e.g., <c>JsonSchemaValidator</c>, <c>XsdValidator</c>, <c>PredicateValidator</c>).</param>
    /// <param name="throwOnFailure">Whether to throw a <see cref="Validation.ValidationException"/> on failure (default: true).</param>
    /// <returns>This definition for chaining.</returns>
    IRouteDefinition Validate(Validation.IMessageValidator validator, bool throwOnFailure = true);

    /// <summary>
    /// Validates the exchange using a predicate function.
    /// On failure, sets <c>ValidationErrors</c> and <c>ValidationResult</c> properties.
    /// </summary>
    /// <param name="predicate">Predicate returning <c>true</c> if valid.</param>
    /// <param name="errorMessage">Error message when predicate returns false (default: "Validation failed").</param>
    /// <param name="throwOnFailure">Whether to throw on failure (default: true).</param>
    /// <returns>This definition for chaining.</returns>
    IRouteDefinition Validate(Func<IExchange, bool> predicate, string errorMessage = "Validation failed", bool throwOnFailure = true);

    /// <summary>
    /// Validates the exchange body against a JSON Schema provided as a string.
    /// Forces JSON format regardless of any URI or file extension heuristics.
    /// </summary>
    /// <param name="schemaJson">JSON Schema as a string.</param>
    /// <param name="throwOnFailure">Whether to throw a <see cref="Validation.ValidationException"/> on failure (default: true).</param>
    /// <returns>This definition for chaining.</returns>
    IRouteDefinition ValidateJsonSchema(string schemaJson, bool throwOnFailure = true);

    /// <summary>
    /// Validates the exchange body against a pre-parsed <see cref="Json.Schema.JsonSchema"/>.
    /// Forces JSON format regardless of any URI or file extension heuristics.
    /// </summary>
    /// <param name="schema">The JSON Schema instance.</param>
    /// <param name="throwOnFailure">Whether to throw on failure (default: true).</param>
    /// <returns>This definition for chaining.</returns>
    IRouteDefinition ValidateJsonSchema(Json.Schema.JsonSchema schema, bool throwOnFailure = true);

    /// <summary>
    /// Validates the exchange body against an XSD provided as a string.
    /// Forces XML format regardless of any URI or file extension heuristics.
    /// </summary>
    /// <param name="xsdContent">XSD schema as a string.</param>
    /// <param name="throwOnFailure">Whether to throw on failure (default: true).</param>
    /// <returns>This definition for chaining.</returns>
    IRouteDefinition ValidateXsd(string xsdContent, bool throwOnFailure = true);

    /// <summary>
    /// Validates the exchange body against an XSD provided as a string with an explicit target namespace.
    /// Forces XML format regardless of any URI or file extension heuristics.
    /// </summary>
    /// <param name="targetNamespace">Target namespace URI (or null for no-namespace schemas).</param>
    /// <param name="xsdContent">XSD schema as a string.</param>
    /// <param name="throwOnFailure">Whether to throw on failure (default: true).</param>
    /// <returns>This definition for chaining.</returns>
    IRouteDefinition ValidateXsd(string? targetNamespace, string xsdContent, bool throwOnFailure = true);

    /// <summary>
    /// Validates the exchange body against a pre-built <see cref="System.Xml.Schema.XmlSchemaSet"/>.
    /// Forces XML format regardless of any URI or file extension heuristics.
    /// </summary>
    /// <param name="schemaSet">A compiled XML Schema set.</param>
    /// <param name="throwOnFailure">Whether to throw on failure (default: true).</param>
    /// <returns>This definition for chaining.</returns>
    IRouteDefinition ValidateXsd(System.Xml.Schema.XmlSchemaSet schemaSet, bool throwOnFailure = true);

    // ── Serialization ──

    /// <summary>Serializes the message body to bytes using the specified serializer type.</summary>
    /// <param name="serializerType">Type implementing <see cref="IMessageSerializer"/>.</param>
    /// <returns>This definition for chaining.</returns>
    IRouteDefinition Marshal(Type serializerType);

    /// <summary>Deserializes the message body from bytes to the specified target type.</summary>
    /// <param name="serializerType">Type implementing <see cref="IMessageSerializer"/>.</param>
    /// <param name="targetType">Target type to deserialize to.</param>
    /// <returns>This definition for chaining.</returns>
    IRouteDefinition Unmarshal(Type serializerType, Type targetType);

    /// <summary>
    /// Deserializes the message body to <typeparamref name="T"/> using the serializer
    /// resolved from <see cref="IDataFormatRegistry"/> by the exchange's ContentType.
    /// </summary>
    /// <typeparam name="T">Target type to deserialize to.</typeparam>
    /// <returns>This definition for chaining.</returns>
    IRouteDefinition Unmarshal<T>();

    /// <summary>
    /// Converts the message body to the specified type using ContentType for encoding hints.
    /// </summary>
    /// <typeparam name="T">Target type for conversion.</typeparam>
    /// <returns>This definition for chaining.</returns>
    IRouteDefinition ConvertBody<T>();

    /// <summary>
    /// Wraps a forward-only Stream body with a seekable cache, enabling re-reads.
    /// Non-Stream bodies pass through unchanged.
    /// </summary>
    /// <param name="spoolThreshold">Optional spool threshold in bytes (null = use default 128 KB).</param>
    /// <returns>This definition for chaining.</returns>
    IRouteDefinition StreamCaching(long? spoolThreshold = null);

    // ── Error Handling (inline) ──

    /// <summary>
    /// Wraps subsequent route steps with a retry policy.
    /// <para>
    /// <b>Counter scope:</b> the retry counter is in-process and per-exchange. It is not persisted
    /// across host restarts and is reset whenever a transactional consumer redelivers the same logical
    /// message. For cross-restart retry budgeting use the broker's dead-letter facilities instead.
    /// </para>
    /// </summary>
    /// <param name="maxRetries">Maximum number of retry attempts.</param>
    /// <param name="initialDelay">Initial delay between retries.</param>
    /// <returns>This definition for chaining.</returns>
    IRouteDefinition Retry(int maxRetries, TimeSpan? initialDelay = null);

    /// <summary>
    /// Configures a dead-letter channel for exchanges that fail processing.
    /// </summary>
    /// <param name="deadLetterUri">Endpoint URI for dead-lettered exchanges.</param>
    /// <returns>This definition for chaining.</returns>
    IRouteDefinition DeadLetterChannel(string deadLetterUri);

    // ── Lifecycle ──

    /// <summary>Rolls back all transacted actions on the exchange without throwing an exception.</summary>
    /// <returns>This definition for chaining.</returns>
    IRouteDefinition RollbackAll();

    /// <summary>
    /// Opens an imperative transaction scope on the exchange.
    /// Use <see cref="CommitTransaction"/> or <see cref="RollbackTransaction"/> to close it.
    /// SQL connections auto-enlist; message brokers defer send until commit.
    /// </summary>
    /// <returns>This definition for chaining.</returns>
    IRouteDefinition BeginTransaction();

    /// <summary>
    /// Opens an imperative transaction scope with the specified policy.
    /// </summary>
    /// <param name="policy">Transaction policy (scope option, timeout, isolation level).</param>
    /// <returns>This definition for chaining.</returns>
    IRouteDefinition BeginTransaction(Transactions.TransactionPolicy policy);

    /// <summary>
    /// Commits the imperative transaction scope and all deferred transport actions.
    /// </summary>
    /// <returns>This definition for chaining.</returns>
    IRouteDefinition CommitTransaction();

    /// <summary>
    /// Rolls back the imperative transaction scope and all deferred transport actions.
    /// </summary>
    /// <returns>This definition for chaining.</returns>
    IRouteDefinition RollbackTransaction();

    /// <summary>Marks this route as transacted with the default <c>Required</c> policy.</summary>
    /// <returns>This definition for chaining.</returns>
    IRouteDefinition Transacted();

    /// <summary>Marks this route as transacted with the specified policy.</summary>
    /// <param name="policy">Transaction policy defining scope option, timeout and isolation level.</param>
    /// <returns>This definition for chaining.</returns>
    IRouteDefinition Transacted(Transactions.TransactionPolicy policy);

    /// <summary>
    /// Marks this route as transacted with a well-known policy name
    /// (<c>"Required"</c>, <c>"RequiresNew"</c>, <c>"Suppress"</c>).
    /// </summary>
    /// <param name="policyName">Case-insensitive policy name.</param>
    /// <returns>This definition for chaining.</returns>
    IRouteDefinition Transacted(string policyName);

    // ── Idempotent Consumer ──

    /// <summary>
    /// Adds an idempotent consumer that deduplicates messages by key.
    /// Duplicates are silently skipped.
    /// </summary>
    /// <param name="keyExtractor">Function extracting the unique key from the exchange.</param>
    /// <param name="repository">Repository for tracking processed keys.</param>
    /// <returns>This definition for chaining.</returns>
    IRouteDefinition IdempotentConsumer(
        Func<IExchange, string> keyExtractor,
        IIdempotentRepository repository);

    /// <summary>
    /// Adds an idempotent consumer with control over duplicate handling.
    /// </summary>
    /// <param name="keyExtractor">Function extracting the unique key from the exchange.</param>
    /// <param name="repository">Repository for tracking processed keys.</param>
    /// <param name="skipDuplicate">True to skip duplicates; false to propagate with CamelDuplicateMessage flag.</param>
    /// <returns>This definition for chaining.</returns>
    IRouteDefinition IdempotentConsumer(
        Func<IExchange, string> keyExtractor,
        IIdempotentRepository repository,
        bool skipDuplicate);

    /// <summary>
    /// Adds an idempotent consumer that resolves its repository by logical name from
    /// <see cref="IIdempotentRepositoryProvider"/> at compile time. Use this overload
    /// when the route definition is written before the repository instance is available
    /// (e.g., reusable route definitions across tenants).
    /// </summary>
    /// <param name="keyExtractor">Function extracting the unique key from the exchange.</param>
    /// <param name="repositoryName">
    /// Logical repository name. Must be registered via
    /// <c>context.AddIdempotentRepository(name, repo)</c> before <c>Start()</c>.
    /// </param>
    /// <param name="skipDuplicate">True to skip duplicates; false to propagate with CamelDuplicateMessage flag.</param>
    /// <returns>This definition for chaining.</returns>
    IRouteDefinition IdempotentConsumer(
        Func<IExchange, string> keyExtractor,
        string repositoryName,
        bool skipDuplicate = true);

    // ── Claim Check ──

    /// <summary>
    /// Performs a claim check operation. Stores or retrieves the exchange body
    /// using the specified repository and operation.
    /// </summary>
    /// <param name="operation">The claim check operation to perform.</param>
    /// <param name="repository">The claim check repository.</param>
    /// <param name="ttl">Optional TTL for stored data (only used by Set/Push).</param>
    /// <returns>This definition for chaining.</returns>
    IRouteDefinition ClaimCheck(
        ClaimCheckOperation operation,
        IClaimCheckRepository repository,
        TimeSpan? ttl = null);

    /// <summary>
    /// Performs a keyed claim check operation (Set/Get/GetAndRemove with an explicit key).
    /// </summary>
    /// <param name="operation">The claim check operation to perform.</param>
    /// <param name="key">Explicit claim key.</param>
    /// <param name="repository">The claim check repository.</param>
    /// <param name="ttl">Optional TTL for stored data (only used by Set).</param>
    /// <returns>This definition for chaining.</returns>
    IRouteDefinition ClaimCheck(
        ClaimCheckOperation operation,
        string key,
        IClaimCheckRepository repository,
        TimeSpan? ttl = null);

    /// <summary>
    /// Shortcut: stores the current body in the claim check repository (auto-key).
    /// Body is replaced with the claim key; original key is stored in headers.
    /// </summary>
    /// <param name="repository">The claim check repository.</param>
    /// <param name="ttl">Optional TTL for stored data.</param>
    /// <returns>This definition for chaining.</returns>
    IRouteDefinition ClaimCheck(IClaimCheckRepository repository, TimeSpan? ttl = null);

    /// <summary>
    /// Shortcut: retrieves and removes the body from the claim check repository
    /// using the claim key from headers.
    /// </summary>
    /// <param name="repository">The claim check repository.</param>
    /// <returns>This definition for chaining.</returns>
    IRouteDefinition ClaimCheckGet(IClaimCheckRepository repository);

    // ── Load Balancer ──

    /// <summary>
    /// Load balance across endpoints using the specified strategy.
    /// </summary>
    /// <param name="strategy">The load balancer strategy (RoundRobin, Random, Failover, etc.).</param>
    /// <param name="uris">Target endpoint URIs to balance across.</param>
    /// <returns>This definition for chaining.</returns>
    IRouteDefinition LoadBalance(ILoadBalancerStrategy strategy, params string[] uris);

    /// <summary>
    /// Load balance with full configuration via builder.
    /// </summary>
    /// <param name="configure">Action to configure endpoints, strategy, etc.</param>
    IRouteDefinition LoadBalance(Action<ILoadBalancerDefinition> configure);

    // ── Scatter-Gather ──

    /// <summary>
    /// Scatter-Gather: send to multiple endpoints and aggregate all responses.
    /// </summary>
    /// <param name="aggregationStrategy">Pair-wise aggregation: (accumulated, current) → merged.</param>
    /// <param name="recipients">Target endpoint URIs to scatter to.</param>
    IRouteDefinition ScatterGather(
        Func<IExchange, IExchange, IExchange> aggregationStrategy,
        params string[] recipients);

    /// <summary>
    /// Scatter-Gather with full configuration via builder.
    /// </summary>
    /// <param name="configure">Action to configure recipients, aggregation, timeout, parallelism, etc.</param>
    IRouteDefinition ScatterGather(Action<IScatterGatherDefinition> configure);

    // ── Normalizer ──

    /// <summary>
    /// Normalizes incoming messages of different formats into a single canonical form.
    /// Each When clause matches a format and transforms it; Otherwise handles the default case.
    /// Compiles to a content-based router (Choice) with Transform steps.
    /// </summary>
    /// <param name="configure">Builder action to define When/Otherwise clauses.</param>
    IRouteDefinition Normalize(Action<INormalizerDefinition> configure);

    // ── Bean / Service Activator ──

    /// <summary>
    /// Resolves a service of type <typeparamref name="TService"/> from the exchange's
    /// scoped ServiceProvider and invokes the specified async method.
    /// </summary>
    /// <typeparam name="TService">The service type to resolve from DI.</typeparam>
    /// <param name="method">Async method receiving the resolved service, exchange, and cancellation token.</param>
    IRouteDefinition Bean<TService>(
        Func<TService, IExchange, CancellationToken, Task> method)
        where TService : class;

    /// <summary>
    /// Resolves a service of type <typeparamref name="TService"/> from the exchange's
    /// scoped ServiceProvider and invokes the specified async method (without CancellationToken).
    /// </summary>
    /// <typeparam name="TService">The service type to resolve from DI.</typeparam>
    /// <param name="method">Async method receiving the resolved service and exchange.</param>
    IRouteDefinition Bean<TService>(
        Func<TService, IExchange, Task> method)
        where TService : class;

    /// <summary>
    /// Resolves a service of type <typeparamref name="TService"/> from the exchange's
    /// scoped ServiceProvider and invokes the specified synchronous method.
    /// </summary>
    /// <typeparam name="TService">The service type to resolve from DI.</typeparam>
    /// <param name="method">Synchronous method receiving the resolved service and exchange.</param>
    IRouteDefinition Bean<TService>(
        Action<TService, IExchange> method)
        where TService : class;

    /// <summary>Marks the current exception as handled so it is not re-thrown.
    /// Can be used inside DoCatch or OnException scopes, or as a standalone pipeline step.</summary>
    /// <returns>This definition for chaining.</returns>
    IRouteDefinition ExceptionHandled();

    /// <summary>Stops the exchange — no further processors execute.</summary>
    /// <returns>This definition for chaining.</returns>
    IRouteDefinition Stop();

    // ── Saga ──

    /// <summary>
    /// Configures a saga with forward actions and compensations.
    /// On failure, compensations for completed steps run in reverse order.
    /// </summary>
    /// <param name="configure">Action to configure saga steps and optional completion callback.</param>
    /// <returns>This definition for chaining.</returns>
    IRouteDefinition Saga(Action<ISagaDefinition> configure);

    // ── Sampling ──

    /// <summary>
    /// Samples messages by count: passes every Nth message, drops the rest.
    /// First message always passes. Messages 1, 1+N, 1+2N, ... are sampled.
    /// </summary>
    /// <param name="messageFrequency">Pass every Nth message (1 = pass all).</param>
    IRouteDefinition Sample(int messageFrequency);

    /// <summary>
    /// Samples messages by time: passes at most one message per period, drops the rest.
    /// First message always passes.
    /// </summary>
    /// <param name="period">Minimum interval between sampled messages.</param>
    IRouteDefinition Sample(TimeSpan period);

    // ── Throttle / Rate Limiting ──

    /// <summary>Limits the exchange processing rate to N per second.</summary>
    /// <param name="maxPerSecond">Maximum exchanges per second.</param>
    /// <returns>This definition for chaining.</returns>
    IRouteDefinition Throttle(int maxPerSecond);

    /// <summary>Limits the exchange processing rate to N per time period.</summary>
    /// <param name="maxPerPeriod">Maximum exchanges per period.</param>
    /// <param name="period">Time period for the rate limit.</param>
    /// <returns>This definition for chaining.</returns>
    IRouteDefinition Throttle(int maxPerPeriod, TimeSpan period);

    /// <summary>Limits the exchange processing rate where max-per-period is resolved from a string expression (e.g. <c>"${property.rateLimit}"</c>).</summary>
    /// <param name="expression">Expression string resolving to an integer (max per period).</param>
    /// <param name="period">Time period for the rate limit (null = 1 second).</param>
    /// <returns>This definition for chaining.</returns>
    IRouteDefinition ThrottleExpression(string expression, TimeSpan? period = null);

    /// <summary>Per-key rate limiter: each key extracted from the exchange gets its own independent throttle.</summary>
    /// <param name="keyExtractor">Function extracting the throttle key from the exchange.</param>
    /// <param name="maxPerPeriod">Maximum exchanges per period per key.</param>
    /// <param name="period">Time period for the rate limit (default: 1 second).</param>
    /// <returns>This definition for chaining.</returns>
    IRouteDefinition Throttle(Func<IExchange, string> keyExtractor, int maxPerPeriod, TimeSpan? period = null);

    // ── Debounce ──

    /// <summary>
    /// Per-key debounce: suppresses rapid-fire messages and forwards only the last
    /// exchange for each key after a quiet period of inactivity.
    /// This is a wrapping step — all subsequent steps form the downstream pipeline.
    /// </summary>
    /// <param name="keyExtractor">Function extracting the debounce key from the exchange.</param>
    /// <param name="quietPeriod">Duration of silence required before forwarding.</param>
    /// <returns>This definition for chaining.</returns>
    IRouteDefinition Debounce(Func<IExchange, string> keyExtractor, TimeSpan quietPeriod);

    // ── Circuit Breaker ──

    /// <summary>
    /// Wraps subsequent steps in a circuit breaker. When consecutive failures exceed the threshold,
    /// the circuit opens and exchanges are routed to the optional fallback.
    /// </summary>
    /// <param name="configure">Action to configure the circuit breaker.</param>
    /// <returns>This definition for chaining.</returns>
    IRouteDefinition CircuitBreaker(Action<ICircuitBreakerDefinition> configure);

    // ── Resequencer ──

    /// <summary>
    /// Resequences exchanges by a key, delivering them in sorted order.
    /// Uses batch mode with configurable size and timeout.
    /// </summary>
    /// <param name="keySelector">Function extracting a numeric sequence key (lower = earlier).</param>
    /// <param name="batchSize">Max exchanges before flushing (default: 100).</param>
    /// <param name="timeout">Max wait time for a complete batch (default: 5 seconds).</param>
    /// <returns>This definition for chaining.</returns>
    IRouteDefinition Resequence(
        Func<IExchange, long> keySelector,
        int batchSize = 100,
        TimeSpan? timeout = null);

    // ── Recipient List ──

    /// <summary>
    /// Sends the exchange to a dynamic list of endpoints computed at runtime.
    /// Unlike Multicast, the URI list is determined from the exchange data.
    /// </summary>
    /// <param name="recipientListFactory">Function returning target URIs from the exchange.</param>
    /// <param name="parallelProcessing">Whether to send in parallel.</param>
    /// <param name="stopOnException">Whether to stop on the first exception.</param>
    /// <param name="aggregationStrategy">Optional pair-wise aggregation of results.</param>
    /// <returns>This definition for chaining.</returns>
    IRouteDefinition RecipientList(
        Func<IExchange, IEnumerable<string>> recipientListFactory,
        bool parallelProcessing = false,
        bool stopOnException = false,
        Func<IExchange, IExchange, IExchange>? aggregationStrategy = null);

    // ── Enrich / PollEnrich ──

    /// <summary>
    /// Calls an external endpoint and merges the response into the current exchange.
    /// Implements the Content Enricher EIP pattern.
    /// </summary>
    /// <param name="resourceUri">URI of the enrichment endpoint.</param>
    /// <param name="mergeStrategy">Merge function: (original, enriched) → merged.</param>
    /// <returns>This definition for chaining.</returns>
    IRouteDefinition Enrich(
        string resourceUri,
        Func<IExchange, IExchange, IExchange> mergeStrategy);

    /// <summary>
    /// Polls an endpoint for data and merges the result into the current exchange.
    /// If no data arrives within the timeout, the merge receives a null exchange.
    /// </summary>
    /// <param name="resourceUri">URI of the polling endpoint.</param>
    /// <param name="mergeStrategy">Merge function: (original, polled?) → merged.</param>
    /// <param name="timeout">Timeout waiting for data (default: 1 second).</param>
    /// <returns>This definition for chaining.</returns>
    IRouteDefinition PollEnrich(
        string resourceUri,
        Func<IExchange, IExchange?, IExchange> mergeStrategy,
        TimeSpan? timeout = null);

    // ── Dynamic Router ──

    /// <summary>
    /// Routes the exchange through a chain of endpoints determined dynamically.
    /// The routing function is called after each hop; returns <c>null</c> to stop.
    /// </summary>
    /// <param name="routingFunction">Function returning the next URI or null to stop.</param>
    /// <returns>This definition for chaining.</returns>
    IRouteDefinition DynamicRouter(Func<IExchange, string?> routingFunction);

    // ── Fluent Chain: Content-Based Routing ──

    /// <summary>
    /// Starts a content-based router scope. Use When/Otherwise/End() to define branches.
    /// </summary>
    /// <returns>Choice scope for fluent chaining.</returns>
    IRouteDefinition Choice();

    /// <summary>Adds a when-branch in a Choice scope. Subsequent steps form the branch body.</summary>
    /// <param name="predicate">Condition to evaluate.</param>
    /// <returns>When scope for chaining.</returns>
    IRouteDefinition When(Func<IExchange, bool> predicate);

    /// <summary>Adds a when-branch in a Choice scope using an <see cref="IPredicate"/>.</summary>
    /// <param name="predicate">Predicate to evaluate.</param>
    /// <returns>When scope for chaining.</returns>
    IRouteDefinition When(IPredicate predicate);

    /// <summary>Adds a when-branch in a Choice scope using a string expression.</summary>
    /// <param name="expression">Expression string evaluated as boolean.</param>
    /// <returns>When scope for chaining.</returns>
    IRouteDefinition When(string expression);

    /// <summary>Starts the otherwise (fallback) branch in a Choice scope.</summary>
    /// <returns>Otherwise scope for chaining.</returns>
    IRouteDefinition Otherwise();

    /// <summary>Ends a Choice block and returns to the parent scope.</summary>
    /// <returns>Parent definition for chaining.</returns>
    IRouteDefinition EndChoice();

    // ── Fluent Chain: Loop ──

    /// <summary>Starts a count-based loop scope. Subsequent steps form the loop body until End().</summary>
    /// <param name="count">Number of iterations.</param>
    /// <param name="copy">If true, each iteration receives a clone of the original exchange.</param>
    /// <param name="shareScope">If true (default), copy-mode iterations share the parent's DI scope.</param>
    /// <returns>Loop scope for chaining.</returns>
    IRouteDefinition Loop(int count, bool copy = false, bool shareScope = true);

    /// <summary>Starts a predicate-based loop scope. Subsequent steps form the loop body until End().</summary>
    /// <param name="condition">Predicate evaluated before each iteration.</param>
    /// <param name="copy">If true, each iteration receives a clone of the original exchange.</param>
    /// <param name="shareScope">If true (default), copy-mode iterations share the parent's DI scope.</param>
    /// <returns>Loop scope for chaining.</returns>
    IRouteDefinition Loop(Func<IExchange, bool> condition, bool copy = false, bool shareScope = true);

    // ── Fluent Chain: Try-Catch-Finally ──

    /// <summary>
    /// Starts a try block scope. Subsequent steps are the try body.
    /// Use DoCatch/DoFinally/End() to add error handling.
    /// </summary>
    /// <returns>Try scope for chaining.</returns>
    IRouteDefinition DoTry();

    /// <summary>Starts a typed catch block within a DoTry scope.</summary>
    /// <typeparam name="TException">Exception type to catch.</typeparam>
    /// <returns>Catch scope for chaining.</returns>
    IRouteDefinition DoCatch<TException>() where TException : Exception;

    /// <summary>Starts a catch block for the given exception type within a DoTry scope.</summary>
    /// <param name="exceptionType">Exception type to catch.</param>
    /// <returns>Catch scope for chaining.</returns>
    IRouteDefinition DoCatch(Type exceptionType);

    /// <summary>Starts a finally block within a DoTry scope.</summary>
    /// <returns>Finally scope for chaining.</returns>
    IRouteDefinition DoFinally();

    // ── Fluent Chain: Split ──

    /// <summary>
    /// Starts a split scope. Splits the exchange body into multiple parts;
    /// subsequent steps form the processing pipeline for each part.
    /// Use EndSplit() or End() to close the scope.
    /// </summary>
    /// <param name="splitter">Function that splits the body into parts.</param>
    /// <returns>Split scope for chaining.</returns>
    IRouteDefinition Split(Func<IExchange, IEnumerable<object?>> splitter);

    /// <summary>
    /// Starts a split scope using an <see cref="IExpression"/> to produce an iterable.
    /// </summary>
    /// <param name="expression">Expression returning a collection.</param>
    /// <returns>Split scope for chaining.</returns>
    IRouteDefinition Split(IExpression expression);

    /// <summary>
    /// Starts a streaming split scope using an async enumerable (no buffering).
    /// Subsequent steps form the processing pipeline for each part.
    /// Use EndSplit() or End() to close the scope.
    /// </summary>
    /// <param name="splitter">Function returning an IAsyncEnumerable of parts.</param>
    /// <returns>Split scope for chaining.</returns>
    IRouteDefinition Split(Func<IExchange, IAsyncEnumerable<object?>> splitter);

    /// <summary>Ends a Split block and returns to the parent scope.</summary>
    /// <returns>Parent definition for chaining.</returns>
    IRouteDefinition EndSplit();

    // ── Fluent Chain: OnException ──

    /// <summary>
    /// Starts a global exception handler scope for the given exception type.
    /// Subsequent steps form the handler pipeline. Use MaximumRedeliveries(),
    /// RedeliveryDelay(), etc. to configure retry behaviour.
    /// </summary>
    /// <typeparam name="TException">Exception type to handle.</typeparam>
    /// <returns>OnException scope for chaining.</returns>
    IRouteDefinition OnException<TException>() where TException : Exception;

    /// <summary>
    /// Starts a global exception handler scope for multiple exception types.
    /// One handler definition applies to all specified types.
    /// </summary>
    /// <param name="exceptionTypes">Exception types to handle.</param>
    /// <returns>OnException scope for chaining.</returns>
    IRouteDefinition OnException(params Type[] exceptionTypes);

    /// <summary>Applies a pre-configured redelivery policy to the current OnException scope.</summary>
    /// <param name="policy">Redelivery policy object.</param>
    /// <returns>OnException scope for chaining.</returns>
    IRouteDefinition RedeliveryPolicy(Definitions.RedeliveryPolicy policy);

    /// <summary>
    /// Sets the maximum redelivery attempts within an OnException scope.
    /// <para>
    /// <b>Counter scope:</b> the redelivery counter is in-process and per-exchange. It is reset
    /// every time the same logical message is re-fetched from a transactional broker (Kafka rebalance,
    /// RabbitMQ requeue, etc.). For broker-level retry budgeting use the broker's own dead-letter
    /// or delivery-attempt headers in addition to this setting.
    /// </para>
    /// </summary>
    /// <param name="count">Max redelivery attempts (default: 0).</param>
    /// <returns>OnException scope for chaining.</returns>
    IRouteDefinition MaximumRedeliveries(int count);

    /// <summary>Sets the delay between redelivery attempts within an OnException scope.</summary>
    /// <param name="delay">Delay between retries.</param>
    /// <returns>OnException scope for chaining.</returns>
    IRouteDefinition RedeliveryDelay(TimeSpan delay);

    /// <summary>Sets the backoff multiplier for redelivery delay within an OnException scope.</summary>
    /// <param name="multiplier">Backoff multiplier (default: 1.0 = fixed).</param>
    /// <returns>OnException scope for chaining.</returns>
    IRouteDefinition BackOffMultiplier(double multiplier);

    /// <summary>Enables exponential backoff for redelivery within an OnException scope.</summary>
    /// <returns>OnException scope for chaining.</returns>
    IRouteDefinition UseExponentialBackOff();

    /// <summary>
    /// Marks the exception as handled — it will not be re-thrown after the handler runs.
    /// </summary>
    /// <param name="value">Whether the exception is handled (default: true).</param>
    /// <returns>OnException scope for chaining.</returns>
    IRouteDefinition Handled(bool value = true);

    /// <summary>
    /// Continues the route pipeline after the exception handler runs.
    /// </summary>
    /// <param name="value">Whether to continue (default: true).</param>
    /// <returns>OnException scope for chaining.</returns>
    IRouteDefinition Continued(bool value = true);

    /// <summary>
    /// Sets a guard predicate — the handler fires only when the predicate returns true.
    /// </summary>
    /// <param name="predicate">Predicate evaluated against the exchange.</param>
    /// <returns>OnException scope for chaining.</returns>
    IRouteDefinition OnWhen(Func<IExchange, bool> predicate);

    /// <summary>Sets the log level used for retry attempt messages (default: Warning).</summary>
    /// <param name="level">Log level.</param>
    /// <returns>OnException scope for chaining.</returns>
    IRouteDefinition RetryAttemptedLogLevel(LogLevel level);

    /// <summary>Sets the log level used when all retries are exhausted (default: Error).</summary>
    /// <param name="level">Log level.</param>
    /// <returns>OnException scope for chaining.</returns>
    IRouteDefinition RetriesExhaustedLogLevel(LogLevel level);

    /// <summary>Registers a callback invoked each time the exception occurs (before retry or handler).</summary>
    /// <param name="action">Callback receiving the exchange.</param>
    /// <returns>OnException scope for chaining.</returns>
    IRouteDefinition OnExceptionOccurred(Action<IExchange> action);

    /// <summary>Retry while the predicate returns true, regardless of max redeliveries count.</summary>
    /// <param name="predicate">Predicate evaluated before each retry.</param>
    /// <returns>OnException scope for chaining.</returns>
    IRouteDefinition RetryWhile(Func<IExchange, bool> predicate);

    /// <summary>Callback invoked before each redelivery attempt (after delay, before retry).</summary>
    /// <param name="action">Callback receiving the exchange.</param>
    /// <returns>OnException scope for chaining.</returns>
    IRouteDefinition OnRedelivery(Action<IExchange> action);

    /// <summary>Callback invoked before the exchange is sent to the handler/DLQ after all retries are exhausted.</summary>
    /// <param name="action">Callback receiving the exchange.</param>
    /// <returns>OnException scope for chaining.</returns>
    IRouteDefinition OnPrepareFailure(Action<IExchange> action);

    /// <summary>Restore the original message (body and headers) before each retry and before executing the handler.</summary>
    /// <returns>OnException scope for chaining.</returns>
    IRouteDefinition UseOriginalMessage();

    /// <summary>Restore only the original body before each retry and before executing the handler.</summary>
    /// <returns>OnException scope for chaining.</returns>
    IRouteDefinition UseOriginalBody();

    /// <summary>Allow redelivery to continue even when the route is stopping (cancellation requested).</summary>
    /// <param name="value">Whether to allow redelivery while stopping (default: true).</param>
    /// <returns>OnException scope for chaining.</returns>
    IRouteDefinition AllowRedeliveryWhileStopping(bool value = true);

    /// <summary>Whether to include the full stack trace in retry log messages.</summary>
    /// <param name="value">True to log stack traces (default: true).</param>
    /// <returns>OnException scope for chaining.</returns>
    IRouteDefinition LogStackTrace(bool value = true);

    /// <summary>Whether to log when all retries are exhausted.</summary>
    /// <param name="value">True to log exhaustion (default: true).</param>
    /// <returns>OnException scope for chaining.</returns>
    IRouteDefinition LogExhausted(bool value = true);

    /// <summary>Ends an OnException block and returns to the parent scope.</summary>
    /// <returns>Parent definition for chaining.</returns>
    IRouteDefinition EndOnException();

    // ── Fluent Chain: Split Options ──

    /// <summary>Enables parallel processing within a Split scope.</summary>
    /// <param name="parallel">Whether to process in parallel (default: true).</param>
    /// <returns>Split scope for chaining.</returns>
    IRouteDefinition ParallelProcessing(bool parallel = true);

    /// <summary>Sets the maximum degree of parallelism within a Split scope.</summary>
    /// <param name="maxDop">Max concurrent tasks (0 = processor count).</param>
    /// <returns>Split scope for chaining.</returns>
    IRouteDefinition MaxDegreeOfParallelism(int maxDop);

    /// <summary>Sets the aggregation strategy within a Split scope.</summary>
    /// <param name="strategy">Pair-wise aggregation function.</param>
    /// <returns>Split scope for chaining.</returns>
    IRouteDefinition AggregationStrategy(Func<IExchange, IExchange, IExchange> strategy);

    /// <summary>Enables stop-on-exception behaviour within a Split scope.</summary>
    /// <param name="stop">Whether to stop on first exception (default: true).</param>
    /// <returns>Split scope for chaining.</returns>
    IRouteDefinition StopOnException(bool stop = true);

    /// <summary>Sets the timeout within a Split scope.</summary>
    /// <param name="timeout">Timeout duration.</param>
    /// <returns>Split scope for chaining.</returns>
    IRouteDefinition Timeout(TimeSpan timeout);

    // ── Traced (per-step telemetry) ──

    /// <summary>
    /// Wraps an async processing delegate in a named Activity span (OpenTelemetry trace).
    /// The span name supports expression templates (<c>${header.x}</c>, <c>${body}</c>).
    /// </summary>
    /// <param name="spanName">Activity span name (supports <c>${...}</c> expressions).</param>
    /// <param name="processor">Async processing delegate.</param>
    /// <returns>This definition for chaining.</returns>
    IRouteDefinition Traced(string spanName, Func<IExchange, CancellationToken, Task> processor);

    /// <summary>
    /// Wraps a synchronous processing delegate in a named Activity span.
    /// </summary>
    /// <param name="spanName">Activity span name (supports <c>${...}</c> expressions).</param>
    /// <param name="action">Synchronous processing action.</param>
    /// <returns>This definition for chaining.</returns>
    IRouteDefinition Traced(string spanName, Action<IExchange> action);

    /// <summary>
    /// Wraps an IProcessor instance in a named Activity span.
    /// </summary>
    /// <param name="spanName">Activity span name (supports <c>${...}</c> expressions).</param>
    /// <param name="processor">Processor instance to instrument.</param>
    /// <returns>This definition for chaining.</returns>
    IRouteDefinition Traced(string spanName, IProcessor processor);

    /// <summary>
    /// Opens a Traced block scope. All subsequent steps until <see cref="EndTraced"/> will be
    /// wrapped in a single named Activity span.
    /// </summary>
    /// <param name="spanName">Activity span name (supports <c>${...}</c> expressions).</param>
    /// <returns>Traced scope for chaining.</returns>
    IRouteDefinition Traced(string spanName);

    /// <summary>Ends a Traced block and returns to the parent scope.</summary>
    /// <returns>Parent definition for chaining.</returns>
    IRouteDefinition EndTraced();

    // ── Metered (per-step metrics) ──

    /// <summary>
    /// Wraps an async processing delegate with per-step metrics (counter, histogram).
    /// The step name must be static (no <c>${...}</c> expressions) to prevent metric cardinality explosion.
    /// </summary>
    /// <param name="stepName">Static metric step name.</param>
    /// <param name="processor">Async processing delegate.</param>
    /// <returns>This definition for chaining.</returns>
    IRouteDefinition Metered(string stepName, Func<IExchange, CancellationToken, Task> processor);

    /// <summary>
    /// Wraps a synchronous processing delegate with per-step metrics.
    /// </summary>
    /// <param name="stepName">Static metric step name.</param>
    /// <param name="action">Synchronous processing action.</param>
    /// <returns>This definition for chaining.</returns>
    IRouteDefinition Metered(string stepName, Action<IExchange> action);

    /// <summary>
    /// Wraps an IProcessor instance with per-step metrics.
    /// </summary>
    /// <param name="stepName">Static metric step name.</param>
    /// <param name="processor">Processor instance to meter.</param>
    /// <returns>This definition for chaining.</returns>
    IRouteDefinition Metered(string stepName, IProcessor processor);

    /// <summary>
    /// Opens a Metered block scope. All subsequent steps until <see cref="EndMetered"/> will be
    /// wrapped with per-step metrics collection.
    /// The step name must be static (no <c>${...}</c> expressions).
    /// </summary>
    /// <param name="stepName">Static metric step name.</param>
    /// <returns>Metered scope for chaining.</returns>
    IRouteDefinition Metered(string stepName);

    /// <summary>Ends a Metered block and returns to the parent scope.</summary>
    /// <returns>Parent definition for chaining.</returns>
    IRouteDefinition EndMetered();

    // ── Fluent Chain: Saga ──

    /// <summary>
    /// Opens a saga scope. Add steps with <see cref="SagaStep(Action{IExchange}, Action{IExchange})"/>
    /// and close with <see cref="EndSaga"/>.
    /// </summary>
    /// <returns>Saga scope for chaining.</returns>
    IRouteDefinition Saga();

    /// <summary>Adds a synchronous saga step with compensation in a Saga scope.</summary>
    /// <param name="action">Forward action to execute.</param>
    /// <param name="compensate">Compensation action to run on rollback.</param>
    /// <returns>Saga scope for chaining.</returns>
    IRouteDefinition SagaStep(Action<IExchange> action, Action<IExchange> compensate);

    /// <summary>Adds an async saga step with compensation in a Saga scope.</summary>
    /// <param name="action">Forward action to execute.</param>
    /// <param name="compensate">Compensation action to run on rollback.</param>
    /// <returns>Saga scope for chaining.</returns>
    IRouteDefinition SagaStep(
        Func<IExchange, CancellationToken, Task> action,
        Func<IExchange, CancellationToken, Task> compensate);

    /// <summary>Adds a synchronous forward-only saga step (no compensation) in a Saga scope.</summary>
    /// <param name="action">Forward action to execute.</param>
    /// <returns>Saga scope for chaining.</returns>
    IRouteDefinition SagaStep(Action<IExchange> action);

    /// <summary>Adds an async forward-only saga step (no compensation) in a Saga scope.</summary>
    /// <param name="action">Forward action to execute.</param>
    /// <returns>Saga scope for chaining.</returns>
    IRouteDefinition SagaStep(Func<IExchange, CancellationToken, Task> action);

    /// <summary>Sets a synchronous completion callback for the saga scope.</summary>
    /// <param name="callback">Callback to invoke when all steps complete successfully.</param>
    /// <returns>Saga scope for chaining.</returns>
    IRouteDefinition OnSagaCompletion(Action<IExchange> callback);

    /// <summary>Sets an async completion callback for the saga scope.</summary>
    /// <param name="callback">Callback to invoke when all steps complete successfully.</param>
    /// <returns>Saga scope for chaining.</returns>
    IRouteDefinition OnSagaCompletion(Func<IExchange, CancellationToken, Task> callback);

    /// <summary>Ends a Saga block and returns to the parent scope.</summary>
    /// <returns>Parent definition for chaining.</returns>
    IRouteDefinition EndSaga();

    // ── Scope Navigation ──

    /// <summary>
    /// Closes the current block scope (DoTry, Choice, Loop, Split, OnException, Traced, Metered, Saga) and returns to the parent definition.
    /// </summary>
    /// <returns>Parent definition for chaining.</returns>
    IRouteDefinition End();
}

/// <summary>
/// Fluent sub-definition for content-based routing (choice/when/otherwise).
/// </summary>
public interface IChoiceDefinition
{
    /// <summary>Adds a when-clause: if predicate is true, execute the sub-route.</summary>
    /// <param name="predicate">Condition to evaluate.</param>
    /// <param name="configure">Sub-route for this branch.</param>
    /// <returns>This definition for chaining.</returns>
    IChoiceDefinition When(Func<IExchange, bool> predicate, Action<IRouteDefinition> configure);

    /// <summary>Adds a when-clause using an <see cref="IPredicate"/> instance.</summary>
    /// <param name="predicate">Predicate to evaluate.</param>
    /// <param name="configure">Sub-route for this branch.</param>
    /// <returns>This definition for chaining.</returns>
    IChoiceDefinition When(IPredicate predicate, Action<IRouteDefinition> configure);

    /// <summary>Adds a when-clause using a string expression evaluated as boolean.</summary>
    /// <param name="expression">Expression string.</param>
    /// <param name="configure">Sub-route for this branch.</param>
    /// <returns>This definition for chaining.</returns>
    IChoiceDefinition When(string expression, Action<IRouteDefinition> configure);

    /// <summary>Sets the otherwise (fallback) branch.</summary>
    /// <param name="configure">Sub-route for the fallback.</param>
    void Otherwise(Action<IRouteDefinition> configure);
}

/// <summary>
/// Fluent sub-definition for try-catch error handling.
/// </summary>
public interface ITryCatchDefinition
{
    /// <summary>Defines the body of the try block.</summary>
    /// <param name="configure">Sub-route to execute in the try block.</param>
    /// <returns>This definition for chaining.</returns>
    ITryCatchDefinition Try(Action<IRouteDefinition> configure);

    /// <summary>Adds a catch clause for a specific exception type.</summary>
    /// <typeparam name="TException">Exception type to catch.</typeparam>
    /// <param name="handler">Handler action receiving the exchange (which has Exception set).</param>
    /// <returns>This definition for chaining.</returns>
    ITryCatchDefinition Catch<TException>(Action<IRouteDefinition> handler) where TException : Exception;

    /// <summary>Sets the finally block that always executes.</summary>
    /// <param name="configure">Sub-route for the finally block.</param>
    void Finally(Action<IRouteDefinition> configure);
}

/// <summary>
/// Fluent sub-definition for global exception handling.
/// </summary>
public interface IOnExceptionDefinition
{
    /// <summary>Registers a handler for a specific exception type.</summary>
    /// <typeparam name="TException">Exception type to handle.</typeparam>
    /// <param name="handler">Handler sub-route.</param>
    /// <param name="maxRedeliveries">Maximum retry attempts before handler fires (default: 0).</param>
    /// <param name="redeliveryDelay">Delay between retry attempts (default: 1 second).</param>
    /// <param name="backoffMultiplier">Backoff multiplier for delay (default: 1.0 = fixed).</param>
    /// <param name="useExponentialBackoff">Whether to use exponential backoff (default: false).</param>
    /// <returns>This definition for chaining.</returns>
    IOnExceptionDefinition Handle<TException>(
        Action<IRouteDefinition> handler,
        int maxRedeliveries = 0,
        TimeSpan? redeliveryDelay = null,
        double backoffMultiplier = 1.0,
        bool useExponentialBackoff = false)
        where TException : Exception;
}

/// <summary>
/// Fluent sub-definition for configuring a circuit breaker.
/// </summary>
public interface ICircuitBreakerDefinition
{
    /// <summary>Sets the failure threshold (consecutive failures before opening).</summary>
    /// <param name="threshold">Number of failures.</param>
    /// <returns>This definition for chaining.</returns>
    ICircuitBreakerDefinition Threshold(int threshold);

    /// <summary>Sets the reset timeout (time before probing recovery).</summary>
    /// <param name="timeout">Reset timeout.</param>
    /// <returns>This definition for chaining.</returns>
    ICircuitBreakerDefinition ResetTimeout(TimeSpan timeout);

    /// <summary>Sets the max calls allowed in HalfOpen state.</summary>
    /// <param name="maxCalls">Max probing calls.</param>
    /// <returns>This definition for chaining.</returns>
    ICircuitBreakerDefinition HalfOpenMaxCalls(int maxCalls);

    /// <summary>Sets the fallback sub-route when the circuit is open.</summary>
    /// <param name="configure">Sub-route for the fallback.</param>
    /// <returns>This definition for chaining.</returns>
    ICircuitBreakerDefinition FallBack(Action<IRouteDefinition> configure);
}

using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.Abstractions.Typed;

namespace redb.Route.Definitions;

/// <summary>
/// Typed fluent builder wrapping <see cref="RouteDefinition"/>.
/// Provides type-safe access to <c>TIn</c> body while delegating step recording
/// to the underlying untyped definition. Zero allocation overhead.
/// </summary>
/// <typeparam name="TIn">Expected body type of the incoming message.</typeparam>
public class RouteDefinition<TIn> : IRouteDefinition<TIn>
{
    private readonly RouteDefinition _inner;

    /// <summary>Creates a typed wrapper over an existing definition.</summary>
    /// <param name="inner">Underlying untyped definition.</param>
    public RouteDefinition(RouteDefinition inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    /// <summary>Gets the underlying untyped definition.</summary>
    public RouteDefinition Inner => _inner;

    // ── Typed API ──

    /// <inheritdoc />
    public IRouteDefinition<TIn> Filter(Func<TIn, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        _inner.Filter(exchange => predicate((TIn)exchange.In.Body!));
        return this;
    }

    /// <inheritdoc />
    public IRouteDefinition<TOut> Transform<TOut>(Func<TIn, TOut> transform)
    {
        ArgumentNullException.ThrowIfNull(transform);
        _inner.Transform(exchange => transform((TIn)exchange.In.Body!));
        return new RouteDefinition<TOut>(_inner);
    }

    /// <inheritdoc />
    public IRouteDefinition<TIn> Process(Func<TIn, CancellationToken, Task<TIn>> processor)
    {
        ArgumentNullException.ThrowIfNull(processor);
        _inner.Process(async (exchange, ct) =>
        {
            var result = await processor((TIn)exchange.In.Body!, ct).ConfigureAwait(false);
            exchange.In.Body = result;
        });
        return this;
    }

    /// <inheritdoc />
    public IRouteDefinition<TIn> Process(Action<TIn> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        _inner.Process(exchange => action((TIn)exchange.In.Body!));
        return this;
    }

    // ── Delegated IRouteDefinition (untyped) forwarding ──

    /// <inheritdoc />
    public string? GetRouteId() => _inner.GetRouteId();

    /// <inheritdoc />
    public string? GetFromUri() => _inner.GetFromUri();

    /// <inheritdoc />
    public IRouteContext? GetContext() => _inner.GetContext();

    /// <inheritdoc />
    public IRouteDefinition RouteId(string routeId) { _inner.RouteId(routeId); return this; }

    /// <inheritdoc />
    public IRouteDefinition AutoStart(bool value = true) { _inner.AutoStart(value); return this; }

    /// <inheritdoc />
    public bool GetAutoStart() => _inner.GetAutoStart();

    /// <inheritdoc />
    public IRouteDefinition ProcessingTimeout(TimeSpan timeout) { _inner.ProcessingTimeout(timeout); return this; }

    /// <inheritdoc />
    public IRouteDefinition RoutePolicy(IRoutePolicy policy) { _inner.RoutePolicy(policy); return this; }

    /// <inheritdoc />
    public IRoutePolicy? GetRoutePolicy() => _inner.GetRoutePolicy();

    /// <inheritdoc />
    public IRouteDefinition Cluster(bool value = true) { _inner.Cluster(value); return this; }

    /// <inheritdoc />
    public bool GetCluster() => _inner.GetCluster();

    /// <inheritdoc />
    public IRouteDefinition From(string uri) { _inner.From(uri); return this; }

    /// <inheritdoc />
    public IRouteDefinition To(string uri) { _inner.To(uri); return this; }

    /// <inheritdoc />
    public IRouteDefinition SetBody(object? value) { _inner.SetBody(value); return this; }

    /// <inheritdoc />
    public IRouteDefinition SetBody(Func<IExchange, object?> factory) { _inner.SetBody(factory); return this; }

    /// <inheritdoc />
    public IRouteDefinition SetBody(IExpression expression) { _inner.SetBody(expression); return this; }

    /// <inheritdoc />
    public IRouteDefinition SetBodyExpression(string expression) { _inner.SetBodyExpression(expression); return this; }

    /// <inheritdoc />
    public IRouteDefinition Transform(Func<IExchange, object?> transform) { _inner.Transform(transform); return this; }

    /// <inheritdoc />
    public IRouteDefinition Transform(IExpression expression) { _inner.Transform(expression); return this; }

    /// <inheritdoc />
    public IRouteDefinition TransformExpression(string expression) { _inner.TransformExpression(expression); return this; }

    /// <inheritdoc />
    public IRouteDefinition SetHeader(string key, object? value) { _inner.SetHeader(key, value); return this; }

    /// <inheritdoc />
    public IRouteDefinition SetHeader(string key, Func<IExchange, object?> factory) { _inner.SetHeader(key, factory); return this; }

    /// <inheritdoc />
    public IRouteDefinition SetHeader(string key, IExpression expression) { _inner.SetHeader(key, expression); return this; }

    /// <inheritdoc />
    public IRouteDefinition SetHeaderExpression(string key, string expression) { _inner.SetHeaderExpression(key, expression); return this; }

    /// <inheritdoc />
    public IRouteDefinition RemoveHeader(string key) { _inner.RemoveHeader(key); return this; }

    /// <inheritdoc />
    public IRouteDefinition SetProperty(string key, object? value) { _inner.SetProperty(key, value); return this; }

    /// <inheritdoc />
    public IRouteDefinition SetProperty(string key, Func<IExchange, object?> factory) { _inner.SetProperty(key, factory); return this; }

    /// <inheritdoc />
    public IRouteDefinition SetProperty(string key, IExpression expression) { _inner.SetProperty(key, expression); return this; }

    /// <inheritdoc />
    public IRouteDefinition SetPropertyExpression(string key, string expression) { _inner.SetPropertyExpression(key, expression); return this; }

    /// <inheritdoc />
    public IRouteDefinition RemoveProperty(string key) { _inner.RemoveProperty(key); return this; }

    /// <inheritdoc />
    public IRouteDefinition RemoveBody() { _inner.RemoveBody(); return this; }

    /// <inheritdoc />
    public IRouteDefinition ThrowException() { _inner.ThrowException(); return this; }

    /// <inheritdoc />
    public IRouteDefinition ThrowException(string message) { _inner.ThrowException(message); return this; }

    /// <inheritdoc />
    public IRouteDefinition ThrowException(Exception exception) { _inner.ThrowException(exception); return this; }

    /// <inheritdoc />
    public IRouteDefinition ThrowException(Type exceptionType, string message) { _inner.ThrowException(exceptionType, message); return this; }

    /// <inheritdoc />
    public IRouteDefinition ThrowException<TException>(string? message = null) where TException : Exception, new() { _inner.ThrowException<TException>(message); return this; }

    /// <inheritdoc />
    public IRouteDefinition Filter(Func<IExchange, bool> predicate) { _inner.Filter(predicate); return this; }

    /// <inheritdoc />
    public IRouteDefinition Filter(IPredicate predicate) { _inner.Filter(predicate); return this; }

    /// <inheritdoc />
    public IRouteDefinition Filter(string expression) { _inner.Filter(expression); return this; }

    /// <inheritdoc />
    public IRouteDefinition Process(Func<IExchange, CancellationToken, Task> processor) { _inner.Process(processor); return this; }

    /// <inheritdoc />
    public IRouteDefinition Process(Action<IExchange> action) { _inner.Process(action); return this; }

    /// <inheritdoc />
    public IRouteDefinition Process(IProcessor processor) { _inner.Process(processor); return this; }

    /// <inheritdoc />
    public IRouteDefinition Traced(string spanName, Func<IExchange, CancellationToken, Task> processor) { _inner.Traced(spanName, processor); return this; }

    /// <inheritdoc />
    public IRouteDefinition Traced(string spanName, Action<IExchange> action) { _inner.Traced(spanName, action); return this; }

    /// <inheritdoc />
    public IRouteDefinition Traced(string spanName, IProcessor processor) { _inner.Traced(spanName, processor); return this; }

    /// <inheritdoc />
    public IRouteDefinition Traced(string spanName) => _inner.Traced(spanName);

    /// <inheritdoc />
    public IRouteDefinition EndTraced() => _inner.EndTraced();

    /// <inheritdoc />
    public IRouteDefinition Metered(string stepName, Func<IExchange, CancellationToken, Task> processor) { _inner.Metered(stepName, processor); return this; }

    /// <inheritdoc />
    public IRouteDefinition Metered(string stepName, Action<IExchange> action) { _inner.Metered(stepName, action); return this; }

    /// <inheritdoc />
    public IRouteDefinition Metered(string stepName, IProcessor processor) { _inner.Metered(stepName, processor); return this; }

    /// <inheritdoc />
    public IRouteDefinition Metered(string stepName) => _inner.Metered(stepName);

    /// <inheritdoc />
    public IRouteDefinition EndMetered() => _inner.EndMetered();

    // ── Fluent Chain: Saga ──

    /// <inheritdoc />
    public IRouteDefinition Saga() => _inner.Saga();

    /// <inheritdoc />
    public IRouteDefinition SagaStep(Action<Abstractions.IExchange> action, Action<Abstractions.IExchange> compensate) => _inner.SagaStep(action, compensate);

    /// <inheritdoc />
    public IRouteDefinition SagaStep(
        Func<Abstractions.IExchange, CancellationToken, Task> action,
        Func<Abstractions.IExchange, CancellationToken, Task> compensate) => _inner.SagaStep(action, compensate);

    /// <inheritdoc />
    public IRouteDefinition SagaStep(Action<Abstractions.IExchange> action) => _inner.SagaStep(action);

    /// <inheritdoc />
    public IRouteDefinition SagaStep(Func<Abstractions.IExchange, CancellationToken, Task> action) => _inner.SagaStep(action);

    /// <inheritdoc />
    public IRouteDefinition OnSagaCompletion(Action<Abstractions.IExchange> callback) => _inner.OnSagaCompletion(callback);

    /// <inheritdoc />
    public IRouteDefinition OnSagaCompletion(Func<Abstractions.IExchange, CancellationToken, Task> callback) => _inner.OnSagaCompletion(callback);

    /// <inheritdoc />
    public IRouteDefinition EndSaga() => _inner.EndSaga();

    /// <inheritdoc />
    public IRouteDefinition Choice(Action<IChoiceDefinition> configure) { _inner.Choice(configure); return this; }

    /// <inheritdoc />
    public IRouteDefinition Multicast(params string[] uris) { _inner.Multicast(uris); return this; }

    /// <inheritdoc />
    public IRouteDefinition Multicast(
        string[] uris,
        bool parallelProcessing,
        Func<IExchange, IExchange, IExchange>? aggregationStrategy = null,
        bool stopOnException = false,
        TimeSpan timeout = default,
        int maxDegreeOfParallelism = 0)
    { _inner.Multicast(uris, parallelProcessing, aggregationStrategy, stopOnException, timeout, maxDegreeOfParallelism); return this; }

    /// <inheritdoc />
    public IRouteDefinition WireTap(string uri, Action<IExchange>? onPrepare = null, Func<IExchange, object?>? newBodyFactory = null)
    { _inner.WireTap(uri, onPrepare, newBodyFactory); return this; }

    /// <inheritdoc />
    public IRouteDefinition Split(
        Func<IExchange, IEnumerable<object?>> splitter,
        Action<IRouteDefinition>? configure = null,
        bool parallelProcessing = false,
        int maxDegreeOfParallelism = 0,
        Func<IExchange, IExchange, IExchange>? aggregationStrategy = null,
        bool stopOnException = true,
        TimeSpan timeout = default)
    {
        _inner.Split(splitter, configure, parallelProcessing, maxDegreeOfParallelism, aggregationStrategy, stopOnException, timeout);
        return this;
    }

    /// <inheritdoc />
    public IRouteDefinition Split(
        IExpression expression,
        Action<IRouteDefinition>? configure = null,
        bool parallelProcessing = false,
        int maxDegreeOfParallelism = 0,
        Func<IExchange, IExchange, IExchange>? aggregationStrategy = null,
        bool stopOnException = true,
        TimeSpan timeout = default)
    {
        _inner.Split(expression, configure, parallelProcessing, maxDegreeOfParallelism, aggregationStrategy, stopOnException, timeout);
        return this;
    }

    /// <inheritdoc />
    public IRouteDefinition Aggregate(
        Func<IExchange, string> correlationKey,
        Func<IExchange, IExchange, IExchange> aggregationStrategy,
        Func<IExchange, bool> completionPredicate)
    {
        _inner.Aggregate(correlationKey, aggregationStrategy, completionPredicate);
        return this;
    }

    /// <inheritdoc />
    public IRouteDefinition Loop(int count, Action<IRouteDefinition> configure, bool copy = false, bool shareScope = true) { _inner.Loop(count, configure, copy, shareScope); return this; }

    /// <inheritdoc />
    public IRouteDefinition Loop(Func<IExchange, bool> condition, Action<IRouteDefinition> configure, bool copy = false, bool shareScope = true) { _inner.Loop(condition, configure, copy, shareScope); return this; }

    /// <inheritdoc />
    public IRouteDefinition LoopExpression(string expression, Action<IRouteDefinition> configure, bool copy = false, bool shareScope = true) { _inner.LoopExpression(expression, configure, copy, shareScope); return this; }

    /// <inheritdoc />
    public IRouteDefinition Delay(TimeSpan delay) { _inner.Delay(delay); return this; }

    /// <inheritdoc />
    public IRouteDefinition Delay(Func<IExchange, TimeSpan> factory) { _inner.Delay(factory); return this; }

    /// <inheritdoc />
    public IRouteDefinition DelayExpression(string expression) { _inner.DelayExpression(expression); return this; }

    /// <inheritdoc />
    public IRouteDefinition TryCatch(Action<ITryCatchDefinition> configure) { _inner.TryCatch(configure); return this; }

    /// <inheritdoc />
    public IRouteDefinition OnException(Action<IOnExceptionDefinition> configure) { _inner.OnException(configure); return this; }

    /// <inheritdoc />
    public IRouteDefinition SetPattern(ExchangePattern pattern) { _inner.SetPattern(pattern); return this; }

    /// <inheritdoc />
    public IRouteDefinition Respond(Func<IExchange, object?> factory) { _inner.Respond(factory); return this; }

    /// <inheritdoc />
    public IRouteDefinition Log(string message, LogLevel level = LogLevel.Information) { _inner.Log(message, level); return this; }

    /// <inheritdoc />
    public IRouteDefinition Log(Func<IExchange, string> messageFactory, LogLevel level = LogLevel.Information) { _inner.Log(messageFactory, level); return this; }

    /// <inheritdoc />
    public IRouteDefinition Log(LogLevel level) => _inner.Log(level);

    /// <inheritdoc />
    public IRouteDefinition Message(string message) => _inner.Message(message);

    /// <inheritdoc />
    public IRouteDefinition Message(Func<IExchange, string> messageFunc) => _inner.Message(messageFunc);

    /// <inheritdoc />
    public IRouteDefinition Header(string name) => _inner.Header(name);

    /// <inheritdoc />
    public IRouteDefinition Property(string name) => _inner.Property(name);

    /// <inheritdoc />
    public IRouteDefinition ShowRouteId() => _inner.ShowRouteId();

    /// <inheritdoc />
    public IRouteDefinition EndLog() => _inner.EndLog();

    /// <inheritdoc />
    public IRouteDefinition Marshal(Type serializerType) { _inner.Marshal(serializerType); return this; }

    /// <inheritdoc />
    public IRouteDefinition Unmarshal(Type serializerType, Type targetType) { _inner.Unmarshal(serializerType, targetType); return this; }

    /// <inheritdoc />
    public IRouteDefinition Unmarshal<T>() { _inner.Unmarshal<T>(); return this; }

    /// <inheritdoc />
    public IRouteDefinition ConvertBody<T>() { _inner.ConvertBody<T>(); return this; }

    /// <inheritdoc />
    public IRouteDefinition StreamCaching(long? spoolThreshold = null) { _inner.StreamCaching(spoolThreshold); return this; }

    /// <inheritdoc />
    public IRouteDefinition Validate(Validation.IMessageValidator validator, bool throwOnFailure = true) { _inner.Validate(validator, throwOnFailure); return this; }

    /// <inheritdoc />
    public IRouteDefinition Validate(Func<IExchange, bool> predicate, string errorMessage = "Validation failed", bool throwOnFailure = true) { _inner.Validate(predicate, errorMessage, throwOnFailure); return this; }

    /// <inheritdoc />
    public IRouteDefinition ValidateJsonSchema(string schemaJson, bool throwOnFailure = true) { _inner.ValidateJsonSchema(schemaJson, throwOnFailure); return this; }

    /// <inheritdoc />
    public IRouteDefinition ValidateJsonSchema(Json.Schema.JsonSchema schema, bool throwOnFailure = true) { _inner.ValidateJsonSchema(schema, throwOnFailure); return this; }

    /// <inheritdoc />
    public IRouteDefinition ValidateXsd(string xsdContent, bool throwOnFailure = true) { _inner.ValidateXsd(xsdContent, throwOnFailure); return this; }

    /// <inheritdoc />
    public IRouteDefinition ValidateXsd(string? targetNamespace, string xsdContent, bool throwOnFailure = true) { _inner.ValidateXsd(targetNamespace, xsdContent, throwOnFailure); return this; }

    /// <inheritdoc />
    public IRouteDefinition ValidateXsd(System.Xml.Schema.XmlSchemaSet schemaSet, bool throwOnFailure = true) { _inner.ValidateXsd(schemaSet, throwOnFailure); return this; }

    /// <inheritdoc />
    public IRouteDefinition Retry(int maxRetries, TimeSpan? initialDelay = null) { _inner.Retry(maxRetries, initialDelay); return this; }

    /// <inheritdoc />
    public IRouteDefinition DeadLetterChannel(string deadLetterUri) { _inner.DeadLetterChannel(deadLetterUri); return this; }

    /// <inheritdoc />
    public IRouteDefinition Transacted() { _inner.Transacted(); return this; }

    /// <inheritdoc />
    public IRouteDefinition Transacted(Transactions.TransactionPolicy policy) { _inner.Transacted(policy); return this; }

    /// <inheritdoc />
    public IRouteDefinition Transacted(string policyName) { _inner.Transacted(policyName); return this; }

    /// <inheritdoc />
    public IRouteDefinition IdempotentConsumer(
        Func<Abstractions.IExchange, string> keyExtractor,
        Abstractions.IIdempotentRepository repository)
    { _inner.IdempotentConsumer(keyExtractor, repository); return this; }

    /// <inheritdoc />
    public IRouteDefinition IdempotentConsumer(
        Func<Abstractions.IExchange, string> keyExtractor,
        Abstractions.IIdempotentRepository repository,
        bool skipDuplicate)
    { _inner.IdempotentConsumer(keyExtractor, repository, skipDuplicate); return this; }

    /// <inheritdoc />
    public IRouteDefinition IdempotentConsumer(
        Func<Abstractions.IExchange, string> keyExtractor,
        string repositoryName,
        bool skipDuplicate = true)
    { _inner.IdempotentConsumer(keyExtractor, repositoryName, skipDuplicate); return this; }

    // ── Claim Check ──

    /// <inheritdoc />
    public IRouteDefinition ClaimCheck(
        Abstractions.ClaimCheckOperation operation,
        Abstractions.IClaimCheckRepository repository,
        TimeSpan? ttl = null)
    { _inner.ClaimCheck(operation, repository, ttl); return this; }

    /// <inheritdoc />
    public IRouteDefinition ClaimCheck(
        Abstractions.ClaimCheckOperation operation,
        string key,
        Abstractions.IClaimCheckRepository repository,
        TimeSpan? ttl = null)
    { _inner.ClaimCheck(operation, key, repository, ttl); return this; }

    /// <inheritdoc />
    public IRouteDefinition ClaimCheck(Abstractions.IClaimCheckRepository repository, TimeSpan? ttl = null)
    { _inner.ClaimCheck(repository, ttl); return this; }

    /// <inheritdoc />
    public IRouteDefinition ClaimCheckGet(Abstractions.IClaimCheckRepository repository)
    { _inner.ClaimCheckGet(repository); return this; }

    /// <inheritdoc />
    public IRouteDefinition LoadBalance(Abstractions.ILoadBalancerStrategy strategy, params string[] uris)
    { _inner.LoadBalance(strategy, uris); return this; }

    /// <inheritdoc />
    public IRouteDefinition LoadBalance(Action<Abstractions.ILoadBalancerDefinition> configure)
    { _inner.LoadBalance(configure); return this; }

    /// <inheritdoc />
    public IRouteDefinition ScatterGather(
        Func<Abstractions.IExchange, Abstractions.IExchange, Abstractions.IExchange> aggregationStrategy,
        params string[] recipients)
    { _inner.ScatterGather(aggregationStrategy, recipients); return this; }

    /// <inheritdoc />
    public IRouteDefinition ScatterGather(Action<Abstractions.IScatterGatherDefinition> configure)
    { _inner.ScatterGather(configure); return this; }

    /// <inheritdoc />
    public IRouteDefinition Normalize(Action<Abstractions.INormalizerDefinition> configure)
    { _inner.Normalize(configure); return this; }

    /// <inheritdoc />
    public IRouteDefinition Bean<TService>(
        Func<TService, Abstractions.IExchange, CancellationToken, Task> method)
        where TService : class
    { _inner.Bean(method); return this; }

    /// <inheritdoc />
    public IRouteDefinition Bean<TService>(
        Func<TService, Abstractions.IExchange, Task> method)
        where TService : class
    { _inner.Bean(method); return this; }

    /// <inheritdoc />
    public IRouteDefinition Bean<TService>(
        Action<TService, Abstractions.IExchange> method)
        where TService : class
    { _inner.Bean(method); return this; }

    /// <inheritdoc />
    public IRouteDefinition Saga(Action<Abstractions.ISagaDefinition> configure)
    { _inner.Saga(configure); return this; }

    /// <inheritdoc />
    public IRouteDefinition Stop() { _inner.Stop(); return this; }

    /// <inheritdoc />
    public IRouteDefinition RollbackAll() { _inner.RollbackAll(); return this; }

    /// <inheritdoc />
    public IRouteDefinition BeginTransaction() { _inner.BeginTransaction(); return this; }

    /// <inheritdoc />
    public IRouteDefinition BeginTransaction(Transactions.TransactionPolicy policy) { _inner.BeginTransaction(policy); return this; }

    /// <inheritdoc />
    public IRouteDefinition CommitTransaction() { _inner.CommitTransaction(); return this; }

    /// <inheritdoc />
    public IRouteDefinition RollbackTransaction() { _inner.RollbackTransaction(); return this; }

    /// <inheritdoc />
    public IRouteDefinition ExceptionHandled() { _inner.ExceptionHandled(); return this; }

    /// <inheritdoc />
    public IRouteDefinition Sample(int messageFrequency) { _inner.Sample(messageFrequency); return this; }

    /// <inheritdoc />
    public IRouteDefinition Sample(TimeSpan period) { _inner.Sample(period); return this; }

    /// <inheritdoc />
    public IRouteDefinition Throttle(int maxPerSecond) { _inner.Throttle(maxPerSecond); return this; }

    /// <inheritdoc />
    public IRouteDefinition Throttle(int maxPerPeriod, TimeSpan period) { _inner.Throttle(maxPerPeriod, period); return this; }

    /// <inheritdoc />
    public IRouteDefinition ThrottleExpression(string expression, TimeSpan? period = null) { _inner.ThrottleExpression(expression, period); return this; }

    /// <inheritdoc />
    public IRouteDefinition Throttle(Func<IExchange, string> keyExtractor, int maxPerPeriod, TimeSpan? period = null) { _inner.Throttle(keyExtractor, maxPerPeriod, period); return this; }

    /// <inheritdoc />
    public IRouteDefinition Debounce(Func<IExchange, string> keyExtractor, TimeSpan quietPeriod) { _inner.Debounce(keyExtractor, quietPeriod); return this; }

    /// <inheritdoc />
    public IRouteDefinition CircuitBreaker(Action<ICircuitBreakerDefinition> configure) { _inner.CircuitBreaker(configure); return this; }

    /// <inheritdoc />
    public IRouteDefinition Resequence(
        Func<IExchange, long> keySelector,
        int batchSize = 100,
        TimeSpan? timeout = null)
    { _inner.Resequence(keySelector, batchSize, timeout); return this; }

    /// <inheritdoc />
    public IRouteDefinition RecipientList(
        Func<IExchange, IEnumerable<string>> recipientListFactory,
        bool parallelProcessing = false,
        bool stopOnException = false,
        Func<IExchange, IExchange, IExchange>? aggregationStrategy = null)
    { _inner.RecipientList(recipientListFactory, parallelProcessing, stopOnException, aggregationStrategy); return this; }

    /// <inheritdoc />
    public IRouteDefinition Enrich(
        string resourceUri,
        Func<IExchange, IExchange, IExchange> mergeStrategy)
    { _inner.Enrich(resourceUri, mergeStrategy); return this; }

    /// <inheritdoc />
    public IRouteDefinition PollEnrich(
        string resourceUri,
        Func<IExchange, IExchange?, IExchange> mergeStrategy,
        TimeSpan? timeout = null)
    { _inner.PollEnrich(resourceUri, mergeStrategy, timeout); return this; }

    /// <inheritdoc />
    public IRouteDefinition DynamicRouter(Func<IExchange, string?> routingFunction) { _inner.DynamicRouter(routingFunction); return this; }

    // ── Fluent Chain Delegation ──

    /// <inheritdoc />
    public IRouteDefinition Choice() => _inner.Choice();

    /// <inheritdoc />
    public IRouteDefinition When(Func<IExchange, bool> predicate) => _inner.When(predicate);

    /// <inheritdoc />
    public IRouteDefinition When(IPredicate predicate) => _inner.When(predicate);

    /// <inheritdoc />
    public IRouteDefinition When(string expression) => _inner.When(expression);

    /// <inheritdoc />
    public IRouteDefinition Otherwise() => _inner.Otherwise();

    /// <inheritdoc />
    public IRouteDefinition EndChoice() => _inner.EndChoice();

    /// <inheritdoc />
    public IRouteDefinition Loop(int count, bool copy = false, bool shareScope = true) => _inner.Loop(count, copy, shareScope);

    /// <inheritdoc />
    public IRouteDefinition Loop(Func<IExchange, bool> condition, bool copy = false, bool shareScope = true) => _inner.Loop(condition, copy, shareScope);

    /// <inheritdoc />
    public IRouteDefinition DoTry() => _inner.DoTry();

    /// <inheritdoc />
    public IRouteDefinition DoCatch<TException>() where TException : Exception => _inner.DoCatch<TException>();

    /// <inheritdoc />
    public IRouteDefinition DoCatch(Type exceptionType) => _inner.DoCatch(exceptionType);

    /// <inheritdoc />
    public IRouteDefinition DoFinally() => _inner.DoFinally();

    /// <inheritdoc />
    public IRouteDefinition End() => _inner.End();

    /// <inheritdoc />
    public IRouteDefinition Split(Func<IExchange, IEnumerable<object?>> splitter) => _inner.Split(splitter);

    /// <inheritdoc />
    public IRouteDefinition Split(IExpression expression) => _inner.Split(expression);

    /// <inheritdoc />
    public IRouteDefinition Split(
        Func<IExchange, IAsyncEnumerable<object?>> splitter,
        Action<IRouteDefinition>? configure = null,
        bool stopOnException = true)
    {
        _inner.Split(splitter, configure, stopOnException);
        return this;
    }

    /// <inheritdoc />
    public IRouteDefinition Split(Func<IExchange, IAsyncEnumerable<object?>> splitter) => _inner.Split(splitter);

    /// <inheritdoc />
    public IRouteDefinition EndSplit() => _inner.EndSplit();

    /// <inheritdoc />
    public IRouteDefinition ParallelProcessing(bool parallel = true) => _inner.ParallelProcessing(parallel);

    /// <inheritdoc />
    public IRouteDefinition MaxDegreeOfParallelism(int maxDop) => _inner.MaxDegreeOfParallelism(maxDop);

    /// <inheritdoc />
    public IRouteDefinition AggregationStrategy(Func<IExchange, IExchange, IExchange> strategy) => _inner.AggregationStrategy(strategy);

    /// <inheritdoc />
    public IRouteDefinition StopOnException(bool stop = true) => _inner.StopOnException(stop);

    /// <inheritdoc />
    public IRouteDefinition Timeout(TimeSpan timeout) => _inner.Timeout(timeout);

    /// <inheritdoc />
    public IRouteDefinition OnException<TException>() where TException : Exception => _inner.OnException<TException>();

    /// <inheritdoc />
    public IRouteDefinition OnException(params Type[] exceptionTypes) => _inner.OnException(exceptionTypes);

    /// <inheritdoc />
    public IRouteDefinition RedeliveryPolicy(RedeliveryPolicy policy) => _inner.RedeliveryPolicy(policy);

    /// <inheritdoc />
    public IRouteDefinition MaximumRedeliveries(int count) => _inner.MaximumRedeliveries(count);

    /// <inheritdoc />
    public IRouteDefinition RedeliveryDelay(TimeSpan delay) => _inner.RedeliveryDelay(delay);

    /// <inheritdoc />
    public IRouteDefinition BackOffMultiplier(double multiplier) => _inner.BackOffMultiplier(multiplier);

    /// <inheritdoc />
    public IRouteDefinition UseExponentialBackOff() => _inner.UseExponentialBackOff();

    /// <inheritdoc />
    public IRouteDefinition Handled(bool value = true) => _inner.Handled(value);

    /// <inheritdoc />
    public IRouteDefinition Continued(bool value = true) => _inner.Continued(value);

    /// <inheritdoc />
    public IRouteDefinition OnWhen(Func<IExchange, bool> predicate) => _inner.OnWhen(predicate);

    /// <inheritdoc />
    public IRouteDefinition RetryAttemptedLogLevel(LogLevel level) => _inner.RetryAttemptedLogLevel(level);

    /// <inheritdoc />
    public IRouteDefinition RetriesExhaustedLogLevel(LogLevel level) => _inner.RetriesExhaustedLogLevel(level);

    /// <inheritdoc />
    public IRouteDefinition OnExceptionOccurred(Action<IExchange> action) => _inner.OnExceptionOccurred(action);

    /// <inheritdoc />
    public IRouteDefinition RetryWhile(Func<IExchange, bool> predicate) => _inner.RetryWhile(predicate);

    /// <inheritdoc />
    public IRouteDefinition OnRedelivery(Action<IExchange> action) => _inner.OnRedelivery(action);

    /// <inheritdoc />
    public IRouteDefinition OnPrepareFailure(Action<IExchange> action) => _inner.OnPrepareFailure(action);

    /// <inheritdoc />
    public IRouteDefinition UseOriginalMessage() => _inner.UseOriginalMessage();

    /// <inheritdoc />
    public IRouteDefinition UseOriginalBody() => _inner.UseOriginalBody();

    /// <inheritdoc />
    public IRouteDefinition AllowRedeliveryWhileStopping(bool allow = true) => _inner.AllowRedeliveryWhileStopping(allow);

    /// <inheritdoc />
    public IRouteDefinition LogStackTrace(bool log = true) => _inner.LogStackTrace(log);

    /// <inheritdoc />
    public IRouteDefinition LogExhausted(bool log = true) => _inner.LogExhausted(log);

    /// <inheritdoc />
    public IRouteDefinition EndOnException() => _inner.EndOnException();
}

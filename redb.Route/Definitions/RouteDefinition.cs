using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.Expressions;

#pragma warning disable CS0618 // Obsolete members — implementation must exist for interface backward compat

namespace redb.Route.Definitions;

/// <summary>
/// Fluent builder that records route steps. Compiled into a processor chain by RouteCompiler.
/// This class only records intent — it does NOT execute anything.
/// </summary>
public class RouteDefinition : IRouteDefinition
{
    internal readonly List<RouteStep> _steps = [];
    private string? _routeId;
    private string? _fromUri;
    private bool _autoStart = true;
    private TimeSpan? _processingTimeout;
    private IRoutePolicy? _routePolicy;
    private bool _cluster;
    internal IRouteContext? _context;

    /// <summary>Gets the recorded steps.</summary>
    public IReadOnlyList<RouteStep> Steps => _steps;

    /// <inheritdoc />
    public string? GetRouteId() => _routeId;

    /// <inheritdoc />
    public bool GetAutoStart() => _autoStart;

    /// <inheritdoc />
    public string? GetFromUri() => _fromUri;

    /// <inheritdoc />
    public IRouteContext? GetContext() => _context;

    /// <summary>Gets the per-route processing timeout, or null if not set.</summary>
    public TimeSpan? GetProcessingTimeout() => _processingTimeout;

    /// <summary>Sets the route context for this definition. Called by RouteBuilder during configuration.</summary>
    internal void SetRouteContext(IRouteContext context) => _context = context;

    /// <summary>Gets whether a From endpoint has been set.</summary>
    public bool HasFrom => _fromUri != null;

    /// <summary>Gets whether this route is marked as transacted.</summary>
    public bool IsTransacted { get; private set; }

    // ── Identity ──

    /// <inheritdoc />
    public IRouteDefinition RouteId(string routeId)
    {
        _routeId = routeId ?? throw new ArgumentNullException(nameof(routeId));
        return this;
    }

    /// <inheritdoc />
    public IRouteDefinition AutoStart(bool value = true)
    {
        _autoStart = value;
        return this;
    }

    /// <inheritdoc />
    public IRouteDefinition RoutePolicy(IRoutePolicy policy)
    {
        _routePolicy = policy ?? throw new ArgumentNullException(nameof(policy));
        return this;
    }

    /// <inheritdoc />
    public IRoutePolicy? GetRoutePolicy() => _routePolicy;

    /// <inheritdoc />
    public IRouteDefinition Cluster(bool value = true)
    {
        _cluster = value;
        return this;
    }

    /// <inheritdoc />
    public bool GetCluster() => _cluster;

    /// <summary>
    /// Sets the per-exchange processing timeout for this route.
    /// If not set, falls back to <see cref="Configuration.RouteEngineOptions.DefaultProcessingTimeout"/>.
    /// </summary>
    public IRouteDefinition ProcessingTimeout(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero && timeout != System.Threading.Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(timeout), "Timeout must be positive or Infinite.");
        _processingTimeout = timeout;
        return this;
    }

    // ── Source ──

    /// <inheritdoc />
    public IRouteDefinition From(string uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (_fromUri != null)
            throw new InvalidOperationException("From() can only be called once per route definition.");
        _fromUri = uri;
        _steps.Add(new FromStep(uri));
        return this;
    }

    // ── Destination ──

    /// <inheritdoc />
    public IRouteDefinition To(string uri)
    {
        _steps.Add(new ToStep(uri ?? throw new ArgumentNullException(nameof(uri))));
        return this;
    }

    // ── Transform / Enrich ──

    /// <inheritdoc />
    public IRouteDefinition SetBody(object? value)
    {
        _steps.Add(new SetBodyStaticStep(value));
        return this;
    }

    /// <inheritdoc />
    public IRouteDefinition SetBody(Func<IExchange, object?> factory)
    {
        _steps.Add(new SetBodyFactoryStep(factory ?? throw new ArgumentNullException(nameof(factory))));
        return this;
    }

    /// <inheritdoc />
    public IRouteDefinition SetBody(IExpression expression)
    {
        _steps.Add(new SetBodyExpressionStep(expression ?? throw new ArgumentNullException(nameof(expression))));
        return this;
    }

    /// <inheritdoc />
    public IRouteDefinition SetBodyExpression(string expression)
    {
        _steps.Add(new SetBodyStringExpressionStep(expression ?? throw new ArgumentNullException(nameof(expression))));
        return this;
    }

    /// <inheritdoc />
    public IRouteDefinition Transform(Func<IExchange, object?> transform)
    {
        _steps.Add(new TransformStep(transform ?? throw new ArgumentNullException(nameof(transform))));
        return this;
    }

    /// <inheritdoc />
    public IRouteDefinition Transform(IExpression expression)
    {
        _steps.Add(new TransformExpressionStep(expression ?? throw new ArgumentNullException(nameof(expression))));
        return this;
    }

    /// <inheritdoc />
    public IRouteDefinition TransformExpression(string expression)
    {
        _steps.Add(new TransformStringExpressionStep(expression ?? throw new ArgumentNullException(nameof(expression))));
        return this;
    }

    /// <inheritdoc />
    public IRouteDefinition SetHeader(string key, object? value)
    {
        _steps.Add(new SetHeaderStaticStep(key ?? throw new ArgumentNullException(nameof(key)), value));
        return this;
    }

    /// <inheritdoc />
    public IRouteDefinition SetHeader(string key, Func<IExchange, object?> factory)
    {
        _steps.Add(new SetHeaderFactoryStep(
            key ?? throw new ArgumentNullException(nameof(key)),
            factory ?? throw new ArgumentNullException(nameof(factory))));
        return this;
    }

    /// <inheritdoc />
    public IRouteDefinition SetHeader(string key, IExpression expression)
    {
        _steps.Add(new SetHeaderExpressionStep(
            key ?? throw new ArgumentNullException(nameof(key)),
            expression ?? throw new ArgumentNullException(nameof(expression))));
        return this;
    }

    /// <inheritdoc />
    public IRouteDefinition SetHeaderExpression(string key, string expression)
    {
        _steps.Add(new SetHeaderStringExpressionStep(
            key ?? throw new ArgumentNullException(nameof(key)),
            expression ?? throw new ArgumentNullException(nameof(expression))));
        return this;
    }

    /// <inheritdoc />
    public IRouteDefinition RemoveHeader(string key)
    {
        _steps.Add(new RemoveHeaderStep(key ?? throw new ArgumentNullException(nameof(key))));
        return this;
    }

    /// <inheritdoc />
    public IRouteDefinition SetProperty(string key, object? value)
    {
        _steps.Add(new SetPropertyStaticStep(key ?? throw new ArgumentNullException(nameof(key)), value));
        return this;
    }

    /// <inheritdoc />
    public IRouteDefinition SetProperty(string key, Func<IExchange, object?> factory)
    {
        _steps.Add(new SetPropertyFactoryStep(
            key ?? throw new ArgumentNullException(nameof(key)),
            factory ?? throw new ArgumentNullException(nameof(factory))));
        return this;
    }

    /// <inheritdoc />
    public IRouteDefinition SetProperty(string key, IExpression expression)
    {
        _steps.Add(new SetPropertyExpressionStep(
            key ?? throw new ArgumentNullException(nameof(key)),
            expression ?? throw new ArgumentNullException(nameof(expression))));
        return this;
    }

    /// <inheritdoc />
    public IRouteDefinition SetPropertyExpression(string key, string expression)
    {
        _steps.Add(new SetPropertyStringExpressionStep(
            key ?? throw new ArgumentNullException(nameof(key)),
            expression ?? throw new ArgumentNullException(nameof(expression))));
        return this;
    }

    /// <inheritdoc />
    public IRouteDefinition RemoveProperty(string key)
    {
        _steps.Add(new RemovePropertyStep(key ?? throw new ArgumentNullException(nameof(key))));
        return this;
    }

    /// <inheritdoc />
    public IRouteDefinition RemoveBody()
    {
        _steps.Add(new RemoveBodyStep());
        return this;
    }

    /// <inheritdoc />
    public IRouteDefinition ThrowException()
    {
        _steps.Add(new RethrowExceptionStep());
        return this;
    }

    /// <inheritdoc />
    public IRouteDefinition ThrowException(string message)
    {
        _steps.Add(new ThrowMessageStep(message ?? throw new ArgumentNullException(nameof(message))));
        return this;
    }

    /// <inheritdoc />
    public IRouteDefinition ThrowException(Exception exception)
    {
        _steps.Add(new ThrowExceptionStep(exception ?? throw new ArgumentNullException(nameof(exception))));
        return this;
    }

    /// <inheritdoc />
    public IRouteDefinition ThrowException(Type exceptionType, string message)
    {
        _steps.Add(new ThrowExceptionTypeStep(
            exceptionType ?? throw new ArgumentNullException(nameof(exceptionType)),
            message ?? throw new ArgumentNullException(nameof(message))));
        return this;
    }

    /// <inheritdoc />
    public IRouteDefinition ThrowException<TException>(string? message = null) where TException : Exception, new()
    {
        _steps.Add(message is null
            ? new ThrowExceptionTypeStep(typeof(TException), null!)
            : new ThrowExceptionTypeStep(typeof(TException), message));
        return this;
    }

    // ── Filtering ──

    /// <inheritdoc />
    public IRouteDefinition Filter(Func<IExchange, bool> predicate)
    {
        _steps.Add(new FilterStep(predicate ?? throw new ArgumentNullException(nameof(predicate))));
        return this;
    }

    /// <inheritdoc />
    public IRouteDefinition Filter(IPredicate predicate)
    {
        _steps.Add(new FilterPredicateStep(predicate ?? throw new ArgumentNullException(nameof(predicate))));
        return this;
    }

    /// <inheritdoc />
    public IRouteDefinition Filter(string expression)
    {
        _steps.Add(new FilterExpressionStep(expression ?? throw new ArgumentNullException(nameof(expression))));
        return this;
    }

    // ── Processing ──

    /// <inheritdoc />
    public IRouteDefinition Process(Func<IExchange, CancellationToken, Task> processor)
    {
        _steps.Add(new ProcessAsyncStep(processor ?? throw new ArgumentNullException(nameof(processor))));
        return this;
    }

    /// <inheritdoc />
    public IRouteDefinition Process(Action<IExchange> action)
    {
        _steps.Add(new ProcessSyncStep(action ?? throw new ArgumentNullException(nameof(action))));
        return this;
    }

    /// <inheritdoc />
    public IRouteDefinition Process(IProcessor processor)
    {
        _steps.Add(new ProcessInstanceStep(processor ?? throw new ArgumentNullException(nameof(processor))));
        return this;
    }

    // ── Traced (per-step telemetry) ──

    /// <inheritdoc />
    public IRouteDefinition Traced(string spanName, Func<IExchange, CancellationToken, Task> processor)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(spanName);
        ArgumentNullException.ThrowIfNull(processor);
        _steps.Add(new TracedStep(spanName, [new ProcessAsyncStep(processor)]));
        return this;
    }

    /// <inheritdoc />
    public IRouteDefinition Traced(string spanName, Action<IExchange> action)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(spanName);
        ArgumentNullException.ThrowIfNull(action);
        _steps.Add(new TracedStep(spanName, [new ProcessSyncStep(action)]));
        return this;
    }

    /// <inheritdoc />
    public IRouteDefinition Traced(string spanName, IProcessor processor)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(spanName);
        ArgumentNullException.ThrowIfNull(processor);
        _steps.Add(new TracedStep(spanName, [new ProcessInstanceStep(processor)]));
        return this;
    }

    /// <inheritdoc />
    public IRouteDefinition Traced(string spanName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(spanName);
        return new TracedScope(this, spanName);
    }

    /// <inheritdoc />
    public IRouteDefinition EndTraced()
    {
        if (this is TracedScope ts)
            return ts.PackageAndReturn();
        throw new InvalidOperationException("EndTraced() can only be called within a Traced() block.");
    }

    // ── Metered (per-step metrics) ──

    private static void ValidateStaticStepName(string stepName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stepName);
        if (stepName.Contains("${"))
            throw new ArgumentException(
                "Metered step names must be static. Dynamic ${...} expressions are not allowed to prevent metric cardinality explosion.",
                nameof(stepName));
    }

    /// <inheritdoc />
    public IRouteDefinition Metered(string stepName, Func<IExchange, CancellationToken, Task> processor)
    {
        ValidateStaticStepName(stepName);
        ArgumentNullException.ThrowIfNull(processor);
        _steps.Add(new MeteredStep(stepName, [new ProcessAsyncStep(processor)]));
        return this;
    }

    /// <inheritdoc />
    public IRouteDefinition Metered(string stepName, Action<IExchange> action)
    {
        ValidateStaticStepName(stepName);
        ArgumentNullException.ThrowIfNull(action);
        _steps.Add(new MeteredStep(stepName, [new ProcessSyncStep(action)]));
        return this;
    }

    /// <inheritdoc />
    public IRouteDefinition Metered(string stepName, IProcessor processor)
    {
        ValidateStaticStepName(stepName);
        ArgumentNullException.ThrowIfNull(processor);
        _steps.Add(new MeteredStep(stepName, [new ProcessInstanceStep(processor)]));
        return this;
    }

    /// <inheritdoc />
    public IRouteDefinition Metered(string stepName)
    {
        ValidateStaticStepName(stepName);
        return new MeteredScope(this, stepName);
    }

    /// <inheritdoc />
    public IRouteDefinition EndMetered()
    {
        if (this is MeteredScope ms)
            return ms.PackageAndReturn();
        throw new InvalidOperationException("EndMetered() can only be called within a Metered() block.");
    }

    // ── Fluent Chain: Saga ──

    /// <inheritdoc />
    public IRouteDefinition Saga()
    {
        return new SagaScope(this);
    }

    /// <inheritdoc />
    public IRouteDefinition SagaStep(Action<Abstractions.IExchange> action, Action<Abstractions.IExchange> compensate)
    {
        if (this is not SagaScope ss)
            throw new InvalidOperationException("SagaStep() can only be called within a Saga() block.");
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(compensate);
        ss.Definition.Step(action, compensate);
        return this;
    }

    /// <inheritdoc />
    public IRouteDefinition SagaStep(
        Func<Abstractions.IExchange, CancellationToken, Task> action,
        Func<Abstractions.IExchange, CancellationToken, Task> compensate)
    {
        if (this is not SagaScope ss)
            throw new InvalidOperationException("SagaStep() can only be called within a Saga() block.");
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(compensate);
        ss.Definition.Step(action, compensate);
        return this;
    }

    /// <inheritdoc />
    public IRouteDefinition SagaStep(Action<Abstractions.IExchange> action)
    {
        if (this is not SagaScope ss)
            throw new InvalidOperationException("SagaStep() can only be called within a Saga() block.");
        ArgumentNullException.ThrowIfNull(action);
        ss.Definition.Step(action);
        return this;
    }

    /// <inheritdoc />
    public IRouteDefinition SagaStep(Func<Abstractions.IExchange, CancellationToken, Task> action)
    {
        if (this is not SagaScope ss)
            throw new InvalidOperationException("SagaStep() can only be called within a Saga() block.");
        ArgumentNullException.ThrowIfNull(action);
        ss.Definition.Step(action);
        return this;
    }

    /// <inheritdoc />
    public IRouteDefinition OnSagaCompletion(Action<Abstractions.IExchange> callback)
    {
        if (this is not SagaScope ss)
            throw new InvalidOperationException("OnSagaCompletion() can only be called within a Saga() block.");
        ArgumentNullException.ThrowIfNull(callback);
        ss.Definition.OnCompletion(callback);
        return this;
    }

    /// <inheritdoc />
    public IRouteDefinition OnSagaCompletion(Func<Abstractions.IExchange, CancellationToken, Task> callback)
    {
        if (this is not SagaScope ss)
            throw new InvalidOperationException("OnSagaCompletion() can only be called within a Saga() block.");
        ArgumentNullException.ThrowIfNull(callback);
        ss.Definition.OnCompletion(callback);
        return this;
    }

    /// <inheritdoc />
    public IRouteDefinition EndSaga()
    {
        if (this is SagaScope ss)
            return ss.PackageAndReturn();
        throw new InvalidOperationException("EndSaga() can only be called within a Saga() block.");
    }

    // ── Content-Based Routing ──

    /// <inheritdoc />
    public IRouteDefinition Choice(Action<IChoiceDefinition> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var choiceDef = new ChoiceDefinitionBuilder();
        configure(choiceDef);
        _steps.Add(new ChoiceStep(
            choiceDef.WhenClauses,
            choiceDef.OtherwiseSteps,
            choiceDef.PredicateClauses.Count > 0 ? choiceDef.PredicateClauses : null,
            choiceDef.ExpressionClauses.Count > 0 ? choiceDef.ExpressionClauses : null));
        return this;
    }

    // ── Multicast / WireTap ──

    /// <inheritdoc />
    public IRouteDefinition Multicast(params string[] uris)
    {
        _steps.Add(new MulticastStep(uris ?? throw new ArgumentNullException(nameof(uris))));
        return this;
    }

    /// <inheritdoc />
    public IRouteDefinition Multicast(
        string[] uris,
        bool parallelProcessing,
        Func<IExchange, IExchange, IExchange>? aggregationStrategy = null,
        bool stopOnException = false,
        TimeSpan timeout = default,
        int maxDegreeOfParallelism = 0)
    {
        _steps.Add(new MulticastStep(
            uris ?? throw new ArgumentNullException(nameof(uris)),
            parallelProcessing,
            aggregationStrategy,
            stopOnException,
            timeout,
            maxDegreeOfParallelism));
        return this;
    }

    /// <inheritdoc />
    public IRouteDefinition WireTap(string uri, Action<IExchange>? onPrepare = null, Func<IExchange, object?>? newBodyFactory = null)
    {
        _steps.Add(new WireTapStep(
            uri ?? throw new ArgumentNullException(nameof(uri)),
            onPrepare,
            newBodyFactory));
        return this;
    }

    // ── Split / Aggregate ──

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
        ArgumentNullException.ThrowIfNull(splitter);
        IReadOnlyList<RouteStep>? subSteps = null;
        if (configure != null)
        {
            var subDef = new RouteDefinition();
            configure(subDef);
            subSteps = subDef.Steps;
        }
        _steps.Add(new SplitStep(splitter, subSteps, parallelProcessing, maxDegreeOfParallelism, aggregationStrategy, stopOnException, timeout));
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
        ArgumentNullException.ThrowIfNull(expression);
        IReadOnlyList<RouteStep>? subSteps = null;
        if (configure != null)
        {
            var subDef = new RouteDefinition();
            configure(subDef);
            subSteps = subDef.Steps;
        }
        _steps.Add(new SplitExpressionStep(expression, subSteps, parallelProcessing, maxDegreeOfParallelism, aggregationStrategy, stopOnException, timeout));
        return this;
    }

    /// <inheritdoc />
    public IRouteDefinition Split(
        Func<IExchange, IAsyncEnumerable<object?>> splitter,
        Action<IRouteDefinition>? configure = null,
        bool stopOnException = true)
    {
        ArgumentNullException.ThrowIfNull(splitter);
        IReadOnlyList<RouteStep>? subSteps = null;
        if (configure != null)
        {
            var subDef = new RouteDefinition();
            configure(subDef);
            subSteps = subDef.Steps;
        }
        _steps.Add(new StreamingSplitStep(splitter, subSteps, stopOnException));
        return this;
    }

    /// <inheritdoc />
    public IRouteDefinition Aggregate(
        Func<IExchange, string> correlationKey,
        Func<IExchange, IExchange, IExchange> aggregationStrategy,
        Func<IExchange, bool> completionPredicate)
    {
        _steps.Add(new AggregateStep(
            correlationKey ?? throw new ArgumentNullException(nameof(correlationKey)),
            aggregationStrategy ?? throw new ArgumentNullException(nameof(aggregationStrategy)),
            completionPredicate ?? throw new ArgumentNullException(nameof(completionPredicate))));
        return this;
    }

    // ── Loop / Delay ──

    /// <inheritdoc />
    public IRouteDefinition Loop(int count, Action<IRouteDefinition> configure, bool copy = false, bool shareScope = true)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var subDef = new RouteDefinition();
        configure(subDef);
        _steps.Add(new LoopCountStep(count, subDef.Steps, copy, shareScope));
        return this;
    }

    /// <inheritdoc />
    public IRouteDefinition Loop(Func<IExchange, bool> condition, Action<IRouteDefinition> configure, bool copy = false, bool shareScope = true)
    {
        ArgumentNullException.ThrowIfNull(condition);
        ArgumentNullException.ThrowIfNull(configure);
        var subDef = new RouteDefinition();
        configure(subDef);
        _steps.Add(new LoopWhileStep(condition, subDef.Steps, copy, shareScope));
        return this;
    }

    /// <inheritdoc />
    public IRouteDefinition LoopExpression(string expression, Action<IRouteDefinition> configure, bool copy = false, bool shareScope = true)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var subDef = new RouteDefinition();
        configure(subDef);
        _steps.Add(new LoopCountExpressionStep(expression ?? throw new ArgumentNullException(nameof(expression)), subDef.Steps, copy, shareScope));
        return this;
    }

    /// <inheritdoc />
    public IRouteDefinition Delay(TimeSpan delay)
    {
        _steps.Add(new DelayStep(delay));
        return this;
    }

    /// <inheritdoc />
    public IRouteDefinition Delay(Func<IExchange, TimeSpan> factory)
    {
        _steps.Add(new DelayFactoryStep(factory ?? throw new ArgumentNullException(nameof(factory))));
        return this;
    }

    /// <inheritdoc />
    public IRouteDefinition DelayExpression(string expression)
    {
        _steps.Add(new DelayExpressionStep(expression ?? throw new ArgumentNullException(nameof(expression))));
        return this;
    }

    // ── Error Handling ──

    /// <inheritdoc />
    public IRouteDefinition TryCatch(Action<ITryCatchDefinition> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var tcDef = new TryCatchDefinitionBuilder();
        configure(tcDef);
        _steps.Add(new TryCatchStep(tcDef.BodySteps, tcDef.CatchClauses, tcDef.FinallySteps));
        return this;
    }

    /// <inheritdoc />
    public IRouteDefinition OnException(Action<IOnExceptionDefinition> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var oeDef = new OnExceptionDefinitionBuilder();
        configure(oeDef);
        _steps.Add(new OnExceptionStep(oeDef.Handlers));
        return this;
    }

    // ── Exchange Pattern / Response ──

    /// <inheritdoc />
    public IRouteDefinition SetPattern(ExchangePattern pattern)
    {
        _steps.Add(new SetPatternStep(pattern));
        return this;
    }

    /// <inheritdoc />
    public IRouteDefinition Respond(Func<IExchange, object?> factory)
    {
        _steps.Add(new RespondStep(factory ?? throw new ArgumentNullException(nameof(factory))));
        return this;
    }

    // ── Logging ──

    /// <inheritdoc />
    public IRouteDefinition Log(string message, LogLevel level = LogLevel.Information)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.Contains("${", StringComparison.Ordinal))
            _steps.Add(new LogTemplateStep(message, level));
        else
            _steps.Add(new LogStaticStep(message, level));
        return this;
    }

    /// <inheritdoc />
    public IRouteDefinition Log(Func<IExchange, string> messageFactory, LogLevel level = LogLevel.Information)
    {
        _steps.Add(new LogDynamicStep(messageFactory ?? throw new ArgumentNullException(nameof(messageFactory)), level));
        return this;
    }

    /// <inheritdoc />
    public IRouteDefinition Log(LogLevel level) => new LogScope(this, level);

    /// <inheritdoc />
    public IRouteDefinition Message(string message)
    {
        if (this is LogScope ls) { ls.Messages.Add(message ?? throw new ArgumentNullException(nameof(message))); return this; }
        throw new InvalidOperationException("Message() can only be called within a Log() scope.");
    }

    /// <inheritdoc />
    public IRouteDefinition Message(Func<IExchange, string> messageFunc)
    {
        if (this is LogScope ls) { ls.MessageFuncs.Add(messageFunc ?? throw new ArgumentNullException(nameof(messageFunc))); return this; }
        throw new InvalidOperationException("Message() can only be called within a Log() scope.");
    }

    /// <inheritdoc />
    public IRouteDefinition Header(string name)
    {
        if (this is LogScope ls) { ls.HeaderNames.Add(name ?? throw new ArgumentNullException(nameof(name))); return this; }
        throw new InvalidOperationException("Header() can only be called within a Log() scope.");
    }

    /// <inheritdoc />
    public IRouteDefinition Property(string name)
    {
        if (this is LogScope ls) { ls.PropertyNames.Add(name ?? throw new ArgumentNullException(nameof(name))); return this; }
        throw new InvalidOperationException("Property() can only be called within a Log() scope.");
    }

    /// <inheritdoc />
    public IRouteDefinition ShowRouteId()
    {
        if (this is LogScope ls) { ls.IncludeRouteId = true; return this; }
        throw new InvalidOperationException("ShowRouteId() can only be called within a Log() scope.");
    }

    /// <inheritdoc />
    public IRouteDefinition EndLog()
    {
        if (this is LogScope ls)
            return ls.PackageAndReturn();
        throw new InvalidOperationException("EndLog() can only be called within a Log() scope.");
    }

    // ── Serialization ──

    // ── Validation ──

    /// <inheritdoc />
    public IRouteDefinition Validate(Validation.IMessageValidator validator, bool throwOnFailure = true)
    {
        _steps.Add(new ValidateInstanceStep(
            validator ?? throw new ArgumentNullException(nameof(validator)),
            throwOnFailure));
        return this;
    }

    /// <inheritdoc />
    public IRouteDefinition Validate(Func<IExchange, bool> predicate, string errorMessage = "Validation failed", bool throwOnFailure = true)
    {
        _steps.Add(new ValidatePredicateStep(
            predicate ?? throw new ArgumentNullException(nameof(predicate)),
            errorMessage ?? "Validation failed",
            throwOnFailure));
        return this;
    }

    /// <inheritdoc />
    public IRouteDefinition ValidateJsonSchema(string schemaJson, bool throwOnFailure = true)
    {
        _steps.Add(new ValidateJsonSchemaStringStep(
            schemaJson ?? throw new ArgumentNullException(nameof(schemaJson)),
            throwOnFailure));
        return this;
    }

    /// <inheritdoc />
    public IRouteDefinition ValidateJsonSchema(Json.Schema.JsonSchema schema, bool throwOnFailure = true)
    {
        _steps.Add(new ValidateJsonSchemaObjectStep(
            schema ?? throw new ArgumentNullException(nameof(schema)),
            throwOnFailure));
        return this;
    }

    /// <inheritdoc />
    public IRouteDefinition ValidateXsd(string xsdContent, bool throwOnFailure = true)
    {
        _steps.Add(new ValidateXsdStringStep(
            xsdContent ?? throw new ArgumentNullException(nameof(xsdContent)),
            throwOnFailure));
        return this;
    }

    /// <inheritdoc />
    public IRouteDefinition ValidateXsd(string? targetNamespace, string xsdContent, bool throwOnFailure = true)
    {
        _steps.Add(new ValidateXsdNamespaceStep(
            targetNamespace,
            xsdContent ?? throw new ArgumentNullException(nameof(xsdContent)),
            throwOnFailure));
        return this;
    }

    /// <inheritdoc />
    public IRouteDefinition ValidateXsd(System.Xml.Schema.XmlSchemaSet schemaSet, bool throwOnFailure = true)
    {
        _steps.Add(new ValidateXsdSchemaSetStep(
            schemaSet ?? throw new ArgumentNullException(nameof(schemaSet)),
            throwOnFailure));
        return this;
    }

    // ── Serialization ──

    /// <inheritdoc />
    public IRouteDefinition Marshal(Type serializerType)
    {
        _steps.Add(new MarshalStep(serializerType ?? throw new ArgumentNullException(nameof(serializerType))));
        return this;
    }

    /// <inheritdoc />
    public IRouteDefinition Unmarshal(Type serializerType, Type targetType)
    {
        _steps.Add(new UnmarshalStep(
            serializerType ?? throw new ArgumentNullException(nameof(serializerType)),
            targetType ?? throw new ArgumentNullException(nameof(targetType))));
        return this;
    }

    /// <inheritdoc />
    public IRouteDefinition Unmarshal<T>()
    {
        _steps.Add(new ConvertBodyStep(typeof(T)));
        return this;
    }

    /// <inheritdoc />
    public IRouteDefinition ConvertBody<T>()
    {
        _steps.Add(new ConvertBodyStep(typeof(T)));
        return this;
    }

    /// <inheritdoc />
    public IRouteDefinition StreamCaching(long? spoolThreshold = null)
    {
        _steps.Add(new StreamCachingStep(spoolThreshold));
        return this;
    }

    // ── Error Handling (inline) ──

    /// <inheritdoc />
    public IRouteDefinition Retry(int maxRetries, TimeSpan? initialDelay = null)
    {
        _steps.Add(new RetryStep(maxRetries, initialDelay));
        return this;
    }

    /// <inheritdoc />
    public IRouteDefinition DeadLetterChannel(string deadLetterUri)
    {
        _steps.Add(new DeadLetterChannelStep(deadLetterUri ?? throw new ArgumentNullException(nameof(deadLetterUri))));
        return this;
    }

    // ── Lifecycle ──

    /// <inheritdoc />
    public IRouteDefinition Transacted()
    {
        IsTransacted = true;
        _steps.Add(new TransactedStep());
        return this;
    }

    /// <inheritdoc />
    public IRouteDefinition Transacted(Transactions.TransactionPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        IsTransacted = true;
        _steps.Add(new TransactedStep(policy));
        return this;
    }

    /// <inheritdoc />
    public IRouteDefinition Transacted(string policyName)
    {
        ArgumentNullException.ThrowIfNull(policyName);
        IsTransacted = true;
        _steps.Add(new TransactedStep(Transactions.TransactionPolicy.FromName(policyName)));
        return this;
    }

    // ── Idempotent Consumer ──

    /// <inheritdoc />
    public IRouteDefinition IdempotentConsumer(
        Func<Abstractions.IExchange, string> keyExtractor,
        Abstractions.IIdempotentRepository repository)
    {
        ArgumentNullException.ThrowIfNull(keyExtractor);
        ArgumentNullException.ThrowIfNull(repository);
        _steps.Add(new IdempotentConsumerStep(keyExtractor, repository));
        return this;
    }

    /// <inheritdoc />
    public IRouteDefinition IdempotentConsumer(
        Func<Abstractions.IExchange, string> keyExtractor,
        Abstractions.IIdempotentRepository repository,
        bool skipDuplicate)
    {
        ArgumentNullException.ThrowIfNull(keyExtractor);
        ArgumentNullException.ThrowIfNull(repository);
        _steps.Add(new IdempotentConsumerStep(keyExtractor, repository, skipDuplicate));
        return this;
    }

    /// <inheritdoc />
    public IRouteDefinition IdempotentConsumer(
        Func<Abstractions.IExchange, string> keyExtractor,
        string repositoryName,
        bool skipDuplicate = true)
    {
        ArgumentNullException.ThrowIfNull(keyExtractor);
        ArgumentException.ThrowIfNullOrEmpty(repositoryName);
        _steps.Add(new NamedIdempotentConsumerStep(keyExtractor, repositoryName, skipDuplicate));
        return this;
    }

    // ── Claim Check ──

    /// <inheritdoc />
    public IRouteDefinition ClaimCheck(
        Abstractions.ClaimCheckOperation operation,
        Abstractions.IClaimCheckRepository repository,
        TimeSpan? ttl = null)
    {
        ArgumentNullException.ThrowIfNull(repository);
        _steps.Add(new ClaimCheckStep(repository, operation, Key: null, ttl));
        return this;
    }

    /// <inheritdoc />
    public IRouteDefinition ClaimCheck(
        Abstractions.ClaimCheckOperation operation,
        string key,
        Abstractions.IClaimCheckRepository repository,
        TimeSpan? ttl = null)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(repository);
        _steps.Add(new ClaimCheckStep(repository, operation, key, ttl));
        return this;
    }

    /// <inheritdoc />
    public IRouteDefinition ClaimCheck(Abstractions.IClaimCheckRepository repository, TimeSpan? ttl = null)
    {
        ArgumentNullException.ThrowIfNull(repository);
        _steps.Add(new ClaimCheckStep(repository, Abstractions.ClaimCheckOperation.Set, Key: null, ttl));
        return this;
    }

    /// <inheritdoc />
    public IRouteDefinition ClaimCheckGet(Abstractions.IClaimCheckRepository repository)
    {
        ArgumentNullException.ThrowIfNull(repository);
        _steps.Add(new ClaimCheckStep(repository, Abstractions.ClaimCheckOperation.GetAndRemove));
        return this;
    }

    // ── Load Balancer ──

    /// <inheritdoc />
    public IRouteDefinition LoadBalance(Abstractions.ILoadBalancerStrategy strategy, params string[] uris)
    {
        ArgumentNullException.ThrowIfNull(strategy);
        if (uris is null || uris.Length == 0)
            throw new ArgumentException("At least one endpoint URI is required.", nameof(uris));
        _steps.Add(new LoadBalanceStep(strategy, uris));
        return this;
    }

    /// <inheritdoc />
    public IRouteDefinition LoadBalance(Action<Abstractions.ILoadBalancerDefinition> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var def = new LoadBalancerDefinition();
        configure(def);
        if (def.SelectedStrategy == null)
            throw new InvalidOperationException("Strategy is required for LoadBalance. Use UseRoundRobin(), UseFailover(), etc.");
        if (def.EndpointUris == null || def.EndpointUris.Length == 0)
            throw new InvalidOperationException("At least one endpoint is required for LoadBalance.");
        _steps.Add(new LoadBalanceStep(def.SelectedStrategy, def.EndpointUris));
        return this;
    }

    // ── Scatter-Gather ──

    /// <inheritdoc />
    public IRouteDefinition ScatterGather(
        Func<Abstractions.IExchange, Abstractions.IExchange, Abstractions.IExchange> aggregationStrategy,
        params string[] recipients)
    {
        ArgumentNullException.ThrowIfNull(aggregationStrategy);
        if (recipients is null || recipients.Length == 0)
            throw new ArgumentException("At least one recipient is required.", nameof(recipients));
        _steps.Add(new ScatterGatherStep(recipients, null, aggregationStrategy));
        return this;
    }

    /// <inheritdoc />
    public IRouteDefinition ScatterGather(Action<Abstractions.IScatterGatherDefinition> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var def = new ScatterGatherDefinition();
        configure(def);
        if (def.Strategy == null)
            throw new InvalidOperationException("AggregationStrategy is required for ScatterGather.");
        if (def.StaticRecipients == null && def.DynamicRecipients == null)
            throw new InvalidOperationException("Recipients are required for ScatterGather.");
        _steps.Add(new ScatterGatherStep(
            def.StaticRecipients, def.DynamicRecipients, def.Strategy,
            def.IsParallel, def.MaxDop, def.StopOnEx, def.TimeoutValue));
        return this;
    }

    // ── Normalizer ──

    /// <inheritdoc />
    public IRouteDefinition Normalize(Action<Abstractions.INormalizerDefinition> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var def = new NormalizerDefinition();
        configure(def);
        _steps.Add(def.Build());
        return this;
    }

    // ── Bean / Service Activator ──

    /// <inheritdoc />
    public IRouteDefinition Bean<TService>(
        Func<TService, Abstractions.IExchange, CancellationToken, Task> method)
        where TService : class
    {
        ArgumentNullException.ThrowIfNull(method);
        _steps.Add(new BeanStep(typeof(TService), (svc, exchange, ct) => method((TService)svc, exchange, ct)));
        return this;
    }

    /// <inheritdoc />
    public IRouteDefinition Bean<TService>(
        Func<TService, Abstractions.IExchange, Task> method)
        where TService : class
    {
        ArgumentNullException.ThrowIfNull(method);
        _steps.Add(new BeanStep(typeof(TService), (svc, exchange, _) => method((TService)svc, exchange)));
        return this;
    }

    /// <inheritdoc />
    public IRouteDefinition Bean<TService>(
        Action<TService, Abstractions.IExchange> method)
        where TService : class
    {
        ArgumentNullException.ThrowIfNull(method);
        _steps.Add(new BeanStep(typeof(TService), (svc, exchange, _) =>
        {
            method((TService)svc, exchange);
            return Task.CompletedTask;
        }));
        return this;
    }

    // ── Saga ──

    /// <inheritdoc />
    public IRouteDefinition Saga(Action<Abstractions.ISagaDefinition> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var def = new SagaDefinition();
        configure(def);
        _steps.Add(def.Build());
        return this;
    }

    /// <inheritdoc />
    public IRouteDefinition Stop()
    {
        _steps.Add(new StopStep());
        return this;
    }

    /// <inheritdoc />
    public IRouteDefinition RollbackAll()
    {
        _steps.Add(new RollbackAllStep());
        return this;
    }

    /// <inheritdoc />
    public IRouteDefinition BeginTransaction()
    {
        _steps.Add(new BeginTransactionStep());
        return this;
    }

    /// <inheritdoc />
    public IRouteDefinition BeginTransaction(Transactions.TransactionPolicy policy)
    {
        _steps.Add(new BeginTransactionStep(policy ?? throw new ArgumentNullException(nameof(policy))));
        return this;
    }

    /// <inheritdoc />
    public IRouteDefinition CommitTransaction()
    {
        _steps.Add(new CommitTransactionStep());
        return this;
    }

    /// <inheritdoc />
    public IRouteDefinition RollbackTransaction()
    {
        _steps.Add(new RollbackTransactionStep());
        return this;
    }

    /// <inheritdoc />
    public IRouteDefinition ExceptionHandled()
    {
        _steps.Add(new ExceptionHandledStep());
        return this;
    }

    // ── Sampling ──

    /// <inheritdoc />
    public IRouteDefinition Sample(int messageFrequency)
    {
        if (messageFrequency < 1) throw new ArgumentOutOfRangeException(nameof(messageFrequency), "Must be >= 1.");
        _steps.Add(new SampleCountStep(messageFrequency));
        return this;
    }

    /// <inheritdoc />
    public IRouteDefinition Sample(TimeSpan period)
    {
        if (period <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(period), "Must be positive.");
        _steps.Add(new SamplePeriodStep(period));
        return this;
    }

    // ── Throttle ──

    /// <inheritdoc />
    public IRouteDefinition Throttle(int maxPerSecond)
    {
        if (maxPerSecond <= 0) throw new ArgumentOutOfRangeException(nameof(maxPerSecond));
        _steps.Add(new ThrottleStep(maxPerSecond));
        return this;
    }

    /// <inheritdoc />
    public IRouteDefinition Throttle(int maxPerPeriod, TimeSpan period)
    {
        if (maxPerPeriod <= 0) throw new ArgumentOutOfRangeException(nameof(maxPerPeriod));
        _steps.Add(new ThrottleStep(maxPerPeriod, period));
        return this;
    }

    /// <inheritdoc />
    public IRouteDefinition ThrottleExpression(string expression, TimeSpan? period = null)
    {
        _steps.Add(new ThrottleExpressionStep(expression ?? throw new ArgumentNullException(nameof(expression)), period));
        return this;
    }

    /// <inheritdoc />
    public IRouteDefinition Throttle(Func<IExchange, string> keyExtractor, int maxPerPeriod, TimeSpan? period = null)
    {
        ArgumentNullException.ThrowIfNull(keyExtractor);
        if (maxPerPeriod <= 0) throw new ArgumentOutOfRangeException(nameof(maxPerPeriod));
        _steps.Add(new KeyedThrottleStep(keyExtractor, maxPerPeriod, period));
        return this;
    }

    // ── Debounce ──

    /// <inheritdoc />
    public IRouteDefinition Debounce(Func<IExchange, string> keyExtractor, TimeSpan quietPeriod)
    {
        ArgumentNullException.ThrowIfNull(keyExtractor);
        if (quietPeriod <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(quietPeriod), "Must be positive.");
        _steps.Add(new DebounceStep(keyExtractor, quietPeriod));
        return this;
    }

    // ── Circuit Breaker ──

    /// <inheritdoc />
    public IRouteDefinition CircuitBreaker(Action<ICircuitBreakerDefinition> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var cbDef = new CircuitBreakerDefinitionBuilder();
        configure(cbDef);
        _steps.Add(new CircuitBreakerStep(
            cbDef.FailureThresholdValue,
            cbDef.ResetTimeoutValue,
            cbDef.HalfOpenMaxCallsValue,
            cbDef.FallbackSteps));
        return this;
    }

    // ── Resequencer ──

    /// <inheritdoc />
    public IRouteDefinition Resequence(
        Func<IExchange, long> keySelector,
        int batchSize = 100,
        TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(keySelector);
        _steps.Add(new ResequenceStep(keySelector, batchSize, timeout));
        return this;
    }

    // ── Recipient List ──

    /// <inheritdoc />
    public IRouteDefinition RecipientList(
        Func<IExchange, IEnumerable<string>> recipientListFactory,
        bool parallelProcessing = false,
        bool stopOnException = false,
        Func<IExchange, IExchange, IExchange>? aggregationStrategy = null)
    {
        ArgumentNullException.ThrowIfNull(recipientListFactory);
        _steps.Add(new RecipientListStep(recipientListFactory, parallelProcessing, stopOnException, aggregationStrategy));
        return this;
    }

    // ── Enrich / PollEnrich ──

    /// <inheritdoc />
    public IRouteDefinition Enrich(
        string resourceUri,
        Func<IExchange, IExchange, IExchange> mergeStrategy)
    {
        ArgumentNullException.ThrowIfNull(resourceUri);
        ArgumentNullException.ThrowIfNull(mergeStrategy);
        _steps.Add(new EnrichStep(resourceUri, mergeStrategy));
        return this;
    }

    /// <inheritdoc />
    public IRouteDefinition PollEnrich(
        string resourceUri,
        Func<IExchange, IExchange?, IExchange> mergeStrategy,
        TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(resourceUri);
        ArgumentNullException.ThrowIfNull(mergeStrategy);
        _steps.Add(new PollEnrichStep(resourceUri, mergeStrategy, timeout));
        return this;
    }

    // ── Dynamic Router ──

    /// <inheritdoc />
    public IRouteDefinition DynamicRouter(Func<IExchange, string?> routingFunction)
    {
        ArgumentNullException.ThrowIfNull(routingFunction);
        _steps.Add(new DynamicRouterStep(routingFunction));
        return this;
    }

    // ─── Internal Sub-Definition Builders ───

    private sealed class ChoiceDefinitionBuilder : IChoiceDefinition
    {
        private readonly List<ChoiceWhenClause> _whenClauses = [];
        private readonly List<ChoiceWhenPredicateClause> _predicateClauses = [];
        private readonly List<ChoiceWhenExpressionClause> _expressionClauses = [];
        private IReadOnlyList<RouteStep>? _otherwiseSteps;

        public IReadOnlyList<ChoiceWhenClause> WhenClauses => _whenClauses;
        public IReadOnlyList<ChoiceWhenPredicateClause> PredicateClauses => _predicateClauses;
        public IReadOnlyList<ChoiceWhenExpressionClause> ExpressionClauses => _expressionClauses;
        public IReadOnlyList<RouteStep>? OtherwiseSteps => _otherwiseSteps;

        public IChoiceDefinition When(Func<IExchange, bool> predicate, Action<IRouteDefinition> configure)
        {
            ArgumentNullException.ThrowIfNull(predicate);
            ArgumentNullException.ThrowIfNull(configure);
            var subDef = new RouteDefinition();
            configure(subDef);
            _whenClauses.Add(new ChoiceWhenClause(predicate, subDef.Steps));
            return this;
        }

        public IChoiceDefinition When(IPredicate predicate, Action<IRouteDefinition> configure)
        {
            ArgumentNullException.ThrowIfNull(predicate);
            ArgumentNullException.ThrowIfNull(configure);
            var subDef = new RouteDefinition();
            configure(subDef);
            _predicateClauses.Add(new ChoiceWhenPredicateClause(predicate, subDef.Steps));
            return this;
        }

        public IChoiceDefinition When(string expression, Action<IRouteDefinition> configure)
        {
            ArgumentNullException.ThrowIfNull(expression);
            ArgumentNullException.ThrowIfNull(configure);
            var subDef = new RouteDefinition();
            configure(subDef);
            _expressionClauses.Add(new ChoiceWhenExpressionClause(expression, subDef.Steps));
            return this;
        }

        public void Otherwise(Action<IRouteDefinition> configure)
        {
            ArgumentNullException.ThrowIfNull(configure);
            var subDef = new RouteDefinition();
            configure(subDef);
            _otherwiseSteps = subDef.Steps;
        }
    }

    private sealed class TryCatchDefinitionBuilder : ITryCatchDefinition
    {
        private IReadOnlyList<RouteStep>? _bodySteps;
        private readonly List<TryCatchClause> _catchClauses = [];
        private IReadOnlyList<RouteStep>? _finallySteps;

        public IReadOnlyList<RouteStep> BodySteps => _bodySteps ?? [];
        public IReadOnlyList<TryCatchClause> CatchClauses => _catchClauses;
        public IReadOnlyList<RouteStep>? FinallySteps => _finallySteps;

        public ITryCatchDefinition Try(Action<IRouteDefinition> configure)
        {
            ArgumentNullException.ThrowIfNull(configure);
            var subDef = new RouteDefinition();
            configure(subDef);
            _bodySteps = subDef.Steps;
            return this;
        }

        public ITryCatchDefinition Catch<TException>(Action<IRouteDefinition> handler) where TException : Exception
        {
            ArgumentNullException.ThrowIfNull(handler);
            var subDef = new RouteDefinition();
            handler(subDef);
            _catchClauses.Add(new TryCatchClause(typeof(TException), subDef.Steps));
            return this;
        }

        public void Finally(Action<IRouteDefinition> configure)
        {
            ArgumentNullException.ThrowIfNull(configure);
            var subDef = new RouteDefinition();
            configure(subDef);
            _finallySteps = subDef.Steps;
        }
    }

    private sealed class OnExceptionDefinitionBuilder : IOnExceptionDefinition
    {
        private readonly List<OnExceptionHandler> _handlers = [];
        public IReadOnlyList<OnExceptionHandler> Handlers => _handlers;

        public IOnExceptionDefinition Handle<TException>(
            Action<IRouteDefinition> handler,
            int maxRedeliveries = 0,
            TimeSpan? redeliveryDelay = null,
            double backoffMultiplier = 1.0,
            bool useExponentialBackoff = false)
            where TException : Exception
        {
            ArgumentNullException.ThrowIfNull(handler);
            var subDef = new RouteDefinition();
            handler(subDef);
            _handlers.Add(new OnExceptionHandler(
                typeof(TException), subDef.Steps, maxRedeliveries,
                redeliveryDelay, backoffMultiplier, useExponentialBackoff));
            return this;
        }
    }

    private sealed class CircuitBreakerDefinitionBuilder : ICircuitBreakerDefinition
    {
        public int FailureThresholdValue { get; private set; } = 5;
        public TimeSpan? ResetTimeoutValue { get; private set; }
        public int HalfOpenMaxCallsValue { get; private set; } = 1;
        public IReadOnlyList<RouteStep>? FallbackSteps { get; private set; }

        public ICircuitBreakerDefinition Threshold(int threshold)
        {
            if (threshold <= 0) throw new ArgumentOutOfRangeException(nameof(threshold));
            FailureThresholdValue = threshold;
            return this;
        }

        public ICircuitBreakerDefinition ResetTimeout(TimeSpan timeout)
        {
            ResetTimeoutValue = timeout;
            return this;
        }

        public ICircuitBreakerDefinition HalfOpenMaxCalls(int maxCalls)
        {
            if (maxCalls <= 0) throw new ArgumentOutOfRangeException(nameof(maxCalls));
            HalfOpenMaxCallsValue = maxCalls;
            return this;
        }

        public ICircuitBreakerDefinition FallBack(Action<IRouteDefinition> configure)
        {
            ArgumentNullException.ThrowIfNull(configure);
            var subDef = new RouteDefinition();
            configure(subDef);
            FallbackSteps = subDef.Steps;
            return this;
        }
    }

    // ─── Fluent Chain: Content-Based Routing ─────────────────────

    /// <inheritdoc />
    public IRouteDefinition Choice() => new ChoiceScope(this);

    /// <inheritdoc />
    public IRouteDefinition When(Func<IExchange, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        if (this is WhenScope ws)
        {
            ws.FinalizeClause();
            return new WhenScope(ws.ChoiceOwner, predicate);
        }
        if (this is ChoiceScope cs)
            return new WhenScope(cs, predicate);
        throw new InvalidOperationException("When() can only be called within a Choice() block.");
    }

    /// <inheritdoc />
    public IRouteDefinition When(IPredicate predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        if (this is WhenScope ws)
        {
            ws.FinalizeClause();
            return new WhenScope(ws.ChoiceOwner, predicate);
        }
        if (this is ChoiceScope cs)
            return new WhenScope(cs, predicate);
        throw new InvalidOperationException("When() can only be called within a Choice() block.");
    }

    /// <inheritdoc />
    public IRouteDefinition When(string expression)
    {
        ArgumentNullException.ThrowIfNull(expression);
        if (this is WhenScope ws)
        {
            ws.FinalizeClause();
            return new WhenScope(ws.ChoiceOwner, expression);
        }
        if (this is ChoiceScope cs)
            return new WhenScope(cs, expression);
        throw new InvalidOperationException("When() can only be called within a Choice() block.");
    }

    /// <inheritdoc />
    public IRouteDefinition Otherwise()
    {
        if (this is WhenScope ws)
        {
            ws.FinalizeClause();
            return new OtherwiseScope(ws.ChoiceOwner);
        }
        if (this is ChoiceScope cs)
            return new OtherwiseScope(cs);
        throw new InvalidOperationException("Otherwise() can only be called within a Choice() block.");
    }

    /// <inheritdoc />
    public IRouteDefinition EndChoice()
    {
        if (this is OtherwiseScope os)
        {
            os.ChoiceOwner.SetOtherwise(Steps);
            return os.ChoiceOwner.PackageAndReturn();
        }
        if (this is WhenScope ws)
        {
            ws.FinalizeClause();
            return ws.ChoiceOwner.PackageAndReturn();
        }
        if (this is ChoiceScope cs)
            return cs.PackageAndReturn();
        throw new InvalidOperationException("EndChoice() can only be called within a Choice() block.");
    }

    // ─── Fluent Chain: Loop ──────────────────────────────────────

    /// <inheritdoc />
    public IRouteDefinition Loop(int count, bool copy = false, bool shareScope = true) => new LoopCountScope(this, count, copy, shareScope);

    /// <inheritdoc />
    public IRouteDefinition Loop(Func<IExchange, bool> condition, bool copy = false, bool shareScope = true)
    {
        ArgumentNullException.ThrowIfNull(condition);
        return new LoopWhileScope(this, condition, copy, shareScope);
    }

    // ─── Fluent Chain: Try-Catch-Finally ─────────────────────────

    /// <inheritdoc />
    public IRouteDefinition DoTry() => new TryBodyScope(this);

    /// <inheritdoc />
    public IRouteDefinition DoCatch<TException>() where TException : Exception
        => DoCatch(typeof(TException));

    /// <inheritdoc />
    public IRouteDefinition DoCatch(Type exceptionType)
    {
        ArgumentNullException.ThrowIfNull(exceptionType);
        if (this is CatchBodyScope cbs)
        {
            cbs.FinalizeClause();
            return new CatchBodyScope(cbs.TryOwner, exceptionType);
        }
        if (this is TryBodyScope ts)
            return new CatchBodyScope(ts, exceptionType);
        throw new InvalidOperationException("DoCatch() can only be called within a DoTry() block.");
    }

    /// <inheritdoc />
    public IRouteDefinition DoFinally()
    {
        if (this is CatchBodyScope cbs)
        {
            cbs.FinalizeClause();
            return new FinallyBodyScope(cbs.TryOwner);
        }
        if (this is TryBodyScope ts)
            return new FinallyBodyScope(ts);
        throw new InvalidOperationException("DoFinally() can only be called within a DoTry() block.");
    }

    // ─── Fluent Chain: Split ─────────────────────────────────────

    /// <inheritdoc />
    public IRouteDefinition Split(Func<IExchange, IEnumerable<object?>> splitter)
    {
        ArgumentNullException.ThrowIfNull(splitter);
        return new SplitFuncScope(this, splitter);
    }

    /// <inheritdoc />
    public IRouteDefinition Split(IExpression expression)
    {
        ArgumentNullException.ThrowIfNull(expression);
        return new SplitExprScope(this, expression);
    }

    /// <inheritdoc />
    public IRouteDefinition Split(Func<IExchange, IAsyncEnumerable<object?>> splitter)
    {
        ArgumentNullException.ThrowIfNull(splitter);
        return new StreamingSplitFuncScope(this, splitter);
    }

    /// <inheritdoc />
    public IRouteDefinition EndSplit()
    {
        if (this is SplitFuncScope sfs)
            return sfs.PackageAndReturn();
        if (this is SplitExprScope ses)
            return ses.PackageAndReturn();
        if (this is StreamingSplitFuncScope ssfs)
            return ssfs.PackageAndReturn();
        throw new InvalidOperationException("EndSplit() can only be called within a Split() block.");
    }

    /// <inheritdoc />
    public IRouteDefinition ParallelProcessing(bool parallel = true)
    {
        if (this is SplitFuncScope sfs) { sfs.IsParallel = parallel; return this; }
        if (this is SplitExprScope ses) { ses.IsParallel = parallel; return this; }
        throw new InvalidOperationException("ParallelProcessing() can only be called within a Split() block.");
    }

    /// <inheritdoc />
    public IRouteDefinition MaxDegreeOfParallelism(int maxDop)
    {
        if (this is SplitFuncScope sfs) { sfs.MaxDop = maxDop; return this; }
        if (this is SplitExprScope ses) { ses.MaxDop = maxDop; return this; }
        throw new InvalidOperationException("MaxDegreeOfParallelism() can only be called within a Split() block.");
    }

    /// <inheritdoc />
    public IRouteDefinition AggregationStrategy(Func<IExchange, IExchange, IExchange> strategy)
    {
        ArgumentNullException.ThrowIfNull(strategy);
        if (this is SplitFuncScope sfs) { sfs.AggStrategy = strategy; return this; }
        if (this is SplitExprScope ses) { ses.AggStrategy = strategy; return this; }
        throw new InvalidOperationException("AggregationStrategy() can only be called within a Split() block.");
    }

    /// <inheritdoc />
    public IRouteDefinition StopOnException(bool stop = true)
    {
        if (this is SplitFuncScope sfs) { sfs.StopOnEx = stop; return this; }
        if (this is SplitExprScope ses) { ses.StopOnEx = stop; return this; }
        if (this is StreamingSplitFuncScope ssfs) { ssfs.StopOnEx = stop; return this; }
        throw new InvalidOperationException("StopOnException() can only be called within a Split() block.");
    }

    /// <inheritdoc />
    public IRouteDefinition Timeout(TimeSpan timeout)
    {
        if (this is SplitFuncScope sfs) { sfs.TimeoutValue = timeout; return this; }
        if (this is SplitExprScope ses) { ses.TimeoutValue = timeout; return this; }
        throw new InvalidOperationException("Timeout() can only be called within a Split() block.");
    }

    // ─── Fluent Chain: OnException ───────────────────────────────

    /// <inheritdoc />
    public IRouteDefinition OnException<TException>() where TException : Exception
        => new OnExceptionScope(this, typeof(TException));

    /// <inheritdoc />
    public IRouteDefinition OnException(params Type[] exceptionTypes)
    {
        if (exceptionTypes == null || exceptionTypes.Length == 0)
            throw new ArgumentException("At least one exception type must be specified.", nameof(exceptionTypes));
        foreach (var t in exceptionTypes)
        {
            if (!typeof(Exception).IsAssignableFrom(t))
                throw new ArgumentException($"Type {t.Name} is not an Exception type.", nameof(exceptionTypes));
        }
        return new MultiOnExceptionScope(this, exceptionTypes);
    }

    /// <inheritdoc />
    public IRouteDefinition RedeliveryPolicy(RedeliveryPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (this is IExceptionConfig cfg) { policy.ApplyTo(cfg); return this; }
        throw new InvalidOperationException("RedeliveryPolicy() can only be called within an OnException() block.");
    }

    /// <inheritdoc />
    public IRouteDefinition MaximumRedeliveries(int count)
    {
        if (this is IExceptionConfig cfg) { cfg.MaxRedeliveriesValue = count; return this; }
        throw new InvalidOperationException("MaximumRedeliveries() can only be called within an OnException() block.");
    }

    /// <inheritdoc />
    public IRouteDefinition RedeliveryDelay(TimeSpan delay)
    {
        if (this is IExceptionConfig cfg) { cfg.RedeliveryDelayValue = delay; return this; }
        throw new InvalidOperationException("RedeliveryDelay() can only be called within an OnException() block.");
    }

    /// <inheritdoc />
    public IRouteDefinition BackOffMultiplier(double multiplier)
    {
        if (this is IExceptionConfig cfg) { cfg.BackoffMultiplierValue = multiplier; return this; }
        throw new InvalidOperationException("BackOffMultiplier() can only be called within an OnException() block.");
    }

    /// <inheritdoc />
    public IRouteDefinition UseExponentialBackOff()
    {
        if (this is IExceptionConfig cfg) { cfg.UseExponentialBackoffValue = true; return this; }
        throw new InvalidOperationException("UseExponentialBackOff() can only be called within an OnException() block.");
    }

    /// <inheritdoc />
    public IRouteDefinition Handled(bool value = true)
    {
        if (this is IExceptionConfig cfg) { cfg.HandledValue = value; return this; }
        throw new InvalidOperationException("Handled() can only be called within an OnException() block.");
    }

    /// <inheritdoc />
    public IRouteDefinition Continued(bool value = true)
    {
        if (this is IExceptionConfig cfg) { cfg.ContinuedValue = value; return this; }
        throw new InvalidOperationException("Continued() can only be called within an OnException() block.");
    }

    /// <inheritdoc />
    public IRouteDefinition OnWhen(Func<IExchange, bool> predicate)
    {
        if (this is IExceptionConfig cfg) { cfg.OnWhenPredicateValue = predicate ?? throw new ArgumentNullException(nameof(predicate)); return this; }
        throw new InvalidOperationException("OnWhen() can only be called within an OnException() block.");
    }

    /// <inheritdoc />
    public IRouteDefinition RetryAttemptedLogLevel(LogLevel level)
    {
        if (this is IExceptionConfig cfg) { cfg.RetryAttemptedLogLevelValue = level; return this; }
        throw new InvalidOperationException("RetryAttemptedLogLevel() can only be called within an OnException() block.");
    }

    /// <inheritdoc />
    public IRouteDefinition RetriesExhaustedLogLevel(LogLevel level)
    {
        if (this is IExceptionConfig cfg) { cfg.RetriesExhaustedLogLevelValue = level; return this; }
        throw new InvalidOperationException("RetriesExhaustedLogLevel() can only be called within an OnException() block.");
    }

    /// <inheritdoc />
    public IRouteDefinition OnExceptionOccurred(Action<IExchange> action)
    {
        if (this is IExceptionConfig cfg) { cfg.OnExceptionOccurredCallback = action ?? throw new ArgumentNullException(nameof(action)); return this; }
        throw new InvalidOperationException("OnExceptionOccurred() can only be called within an OnException() block.");
    }

    /// <inheritdoc />
    public IRouteDefinition RetryWhile(Func<IExchange, bool> predicate)
    {
        if (this is IExceptionConfig cfg) { cfg.RetryWhilePredicateValue = predicate ?? throw new ArgumentNullException(nameof(predicate)); return this; }
        throw new InvalidOperationException("RetryWhile() can only be called within an OnException() block.");
    }

    /// <inheritdoc />
    public IRouteDefinition OnRedelivery(Action<IExchange> action)
    {
        if (this is IExceptionConfig cfg) { cfg.OnRedeliveryCallbackValue = action ?? throw new ArgumentNullException(nameof(action)); return this; }
        throw new InvalidOperationException("OnRedelivery() can only be called within an OnException() block.");
    }

    /// <inheritdoc />
    public IRouteDefinition OnPrepareFailure(Action<IExchange> action)
    {
        if (this is IExceptionConfig cfg) { cfg.OnPrepareFailureCallbackValue = action ?? throw new ArgumentNullException(nameof(action)); return this; }
        throw new InvalidOperationException("OnPrepareFailure() can only be called within an OnException() block.");
    }

    /// <inheritdoc />
    public IRouteDefinition UseOriginalMessage()
    {
        if (this is IExceptionConfig cfg) { cfg.UseOriginalMessageValue = true; return this; }
        throw new InvalidOperationException("UseOriginalMessage() can only be called within an OnException() block.");
    }

    /// <inheritdoc />
    public IRouteDefinition UseOriginalBody()
    {
        if (this is IExceptionConfig cfg) { cfg.UseOriginalBodyValue = true; return this; }
        throw new InvalidOperationException("UseOriginalBody() can only be called within an OnException() block.");
    }

    /// <inheritdoc />
    public IRouteDefinition AllowRedeliveryWhileStopping(bool value = true)
    {
        if (this is IExceptionConfig cfg) { cfg.AllowRedeliveryWhileStoppingValue = value; return this; }
        throw new InvalidOperationException("AllowRedeliveryWhileStopping() can only be called within an OnException() block.");
    }

    /// <inheritdoc />
    public IRouteDefinition LogStackTrace(bool value = true)
    {
        if (this is IExceptionConfig cfg) { cfg.LogStackTraceValue = value; return this; }
        throw new InvalidOperationException("LogStackTrace() can only be called within an OnException() block.");
    }

    /// <inheritdoc />
    public IRouteDefinition LogExhausted(bool value = true)
    {
        if (this is IExceptionConfig cfg) { cfg.LogExhaustedValue = value; return this; }
        throw new InvalidOperationException("LogExhausted() can only be called within an OnException() block.");
    }

    /// <inheritdoc />
    public IRouteDefinition EndOnException()
    {
        if (this is OnExceptionScope oes)
            return oes.PackageAndReturn();
        if (this is MultiOnExceptionScope moes)
            return moes.PackageAndReturn();
        throw new InvalidOperationException("EndOnException() can only be called within an OnException() block.");
    }

    // ─── Scope Navigation ────────────────────────────────────────

    /// <inheritdoc />
    public IRouteDefinition End()
    {
        // Try-Catch scopes
        if (this is FinallyBodyScope fs)
        {
            fs.TryOwner.FinallySteps = Steps;
            return fs.TryOwner.PackageAndReturn();
        }
        if (this is CatchBodyScope cbs)
        {
            cbs.FinalizeClause();
            return cbs.TryOwner.PackageAndReturn();
        }
        if (this is TryBodyScope ts)
            return ts.PackageAndReturn();

        // Choice scopes
        if (this is OtherwiseScope os)
        {
            os.ChoiceOwner.SetOtherwise(Steps);
            return os.ChoiceOwner.PackageAndReturn();
        }
        if (this is WhenScope ws)
        {
            ws.FinalizeClause();
            return ws.ChoiceOwner.PackageAndReturn();
        }
        if (this is ChoiceScope cs)
            return cs.PackageAndReturn();

        // Loop scopes
        if (this is LoopCountScope lcs)
        {
            lcs.Parent._steps.Add(new LoopCountStep(lcs.Count, Steps, lcs.Copy, lcs.ShareScope));
            return lcs.Parent;
        }
        if (this is LoopWhileScope lws)
        {
            lws.Parent._steps.Add(new LoopWhileStep(lws.Condition, Steps, lws.Copy, lws.ShareScope));
            return lws.Parent;
        }

        // Split scopes
        if (this is SplitFuncScope sfsc)
            return sfsc.PackageAndReturn();
        if (this is SplitExprScope sesc)
            return sesc.PackageAndReturn();

        // OnException scopes
        if (this is OnExceptionScope oesc)
            return oesc.PackageAndReturn();
        if (this is MultiOnExceptionScope moesc)
            return moesc.PackageAndReturn();

        // Log scopes
        if (this is LogScope lgsc)
            return lgsc.PackageAndReturn();

        // Traced scopes
        if (this is TracedScope tsc)
            return tsc.PackageAndReturn();

        // Metered scopes
        if (this is MeteredScope msc)
            return msc.PackageAndReturn();

        // Saga scopes
        if (this is SagaScope ssc)
            return ssc.PackageAndReturn();

        throw new InvalidOperationException("End() called outside of a block scope (DoTry, Choice, Loop, Split, OnException, Log, Traced, Metered, Saga).");
    }

    // ─── Fluent Chain Scope Classes ──────────────────────────────

    private sealed class TryBodyScope : RouteDefinition
    {
        internal readonly RouteDefinition Parent;
        internal readonly List<TryCatchClause> CatchClauses = [];
        internal IReadOnlyList<RouteStep>? FinallySteps;

        internal TryBodyScope(RouteDefinition parent) { Parent = parent; _context = parent._context; }

        internal RouteDefinition PackageAndReturn()
        {
            Parent._steps.Add(new TryCatchStep(Steps, CatchClauses, FinallySteps));
            return Parent;
        }
    }

    private sealed class CatchBodyScope : RouteDefinition
    {
        internal readonly TryBodyScope TryOwner;
        internal readonly Type ExceptionType;

        internal CatchBodyScope(TryBodyScope tryOwner, Type exceptionType)
        {
            TryOwner = tryOwner;
            ExceptionType = exceptionType;
            _context = tryOwner._context;
        }

        internal void FinalizeClause()
        {
            TryOwner.CatchClauses.Add(new TryCatchClause(ExceptionType, Steps));
        }
    }

    private sealed class FinallyBodyScope : RouteDefinition
    {
        internal readonly TryBodyScope TryOwner;
        internal FinallyBodyScope(TryBodyScope tryOwner) { TryOwner = tryOwner; _context = tryOwner._context; }
    }

    private sealed class ChoiceScope : RouteDefinition
    {
        internal readonly RouteDefinition Parent;
        internal readonly List<ChoiceWhenClause> WhenClauses = [];
        internal readonly List<ChoiceWhenPredicateClause> PredicateClauses = [];
        internal readonly List<ChoiceWhenExpressionClause> ExpressionClauses = [];
        private IReadOnlyList<RouteStep>? _otherwiseSteps;

        internal ChoiceScope(RouteDefinition parent) { Parent = parent; _context = parent._context; }

        internal void SetOtherwise(IReadOnlyList<RouteStep> steps) => _otherwiseSteps = steps;

        internal RouteDefinition PackageAndReturn()
        {
            Parent._steps.Add(new ChoiceStep(
                WhenClauses, _otherwiseSteps,
                PredicateClauses.Count > 0 ? PredicateClauses : null,
                ExpressionClauses.Count > 0 ? ExpressionClauses : null));
            return Parent;
        }
    }

    private sealed class WhenScope : RouteDefinition
    {
        internal readonly ChoiceScope ChoiceOwner;
        private readonly WhenKind _kind;
        private readonly Func<IExchange, bool>? _funcPredicate;
        private readonly IPredicate? _predicateInstance;
        private readonly string? _expression;

        private enum WhenKind { Func, Predicate, Expression }

        internal WhenScope(ChoiceScope owner, Func<IExchange, bool> predicate)
        {
            ChoiceOwner = owner;
            _kind = WhenKind.Func;
            _funcPredicate = predicate;
            _context = owner._context;
        }

        internal WhenScope(ChoiceScope owner, IPredicate predicate)
        {
            ChoiceOwner = owner;
            _kind = WhenKind.Predicate;
            _predicateInstance = predicate;
            _context = owner._context;
        }

        internal WhenScope(ChoiceScope owner, string expression)
        {
            ChoiceOwner = owner;
            _kind = WhenKind.Expression;
            _expression = expression;
            _context = owner._context;
        }

        internal void FinalizeClause()
        {
            switch (_kind)
            {
                case WhenKind.Func:
                    ChoiceOwner.WhenClauses.Add(new ChoiceWhenClause(_funcPredicate!, Steps));
                    break;
                case WhenKind.Predicate:
                    ChoiceOwner.PredicateClauses.Add(new ChoiceWhenPredicateClause(_predicateInstance!, Steps));
                    break;
                case WhenKind.Expression:
                    ChoiceOwner.ExpressionClauses.Add(new ChoiceWhenExpressionClause(_expression!, Steps));
                    break;
            }
        }
    }

    private sealed class OtherwiseScope : RouteDefinition
    {
        internal readonly ChoiceScope ChoiceOwner;
        internal OtherwiseScope(ChoiceScope owner) { ChoiceOwner = owner; _context = owner._context; }
    }

    private sealed class LoopCountScope : RouteDefinition
    {
        internal readonly RouteDefinition Parent;
        internal readonly int Count;
        internal readonly bool Copy;
        internal readonly bool ShareScope;

        internal LoopCountScope(RouteDefinition parent, int count, bool copy, bool shareScope)
        {
            Parent = parent;
            Count = count;
            Copy = copy;
            ShareScope = shareScope;
            _context = parent._context;
        }
    }

    private sealed class LoopWhileScope : RouteDefinition
    {
        internal readonly RouteDefinition Parent;
        internal readonly Func<IExchange, bool> Condition;
        internal readonly bool Copy;
        internal readonly bool ShareScope;

        internal LoopWhileScope(RouteDefinition parent, Func<IExchange, bool> condition, bool copy, bool shareScope)
        {
            Parent = parent;
            Condition = condition;
            Copy = copy;
            ShareScope = shareScope;
            _context = parent._context;
        }
    }

    private sealed class SplitFuncScope : RouteDefinition
    {
        internal readonly RouteDefinition Parent;
        internal readonly Func<IExchange, IEnumerable<object?>> Splitter;
        internal bool IsParallel;
        internal int MaxDop;
        internal Func<IExchange, IExchange, IExchange>? AggStrategy;
        internal bool StopOnEx = true;
        internal TimeSpan TimeoutValue;

        internal SplitFuncScope(RouteDefinition parent, Func<IExchange, IEnumerable<object?>> splitter)
        {
            Parent = parent;
            Splitter = splitter;
            _context = parent._context;
        }

        internal RouteDefinition PackageAndReturn()
        {
            Parent._steps.Add(new SplitStep(
                Splitter, Steps.Count > 0 ? Steps : null,
                IsParallel, MaxDop, AggStrategy, StopOnEx, TimeoutValue));
            return Parent;
        }
    }

    private sealed class SplitExprScope : RouteDefinition
    {
        internal readonly RouteDefinition Parent;
        internal readonly IExpression Expression;
        internal bool IsParallel;
        internal int MaxDop;
        internal Func<IExchange, IExchange, IExchange>? AggStrategy;
        internal bool StopOnEx = true;
        internal TimeSpan TimeoutValue;

        internal SplitExprScope(RouteDefinition parent, IExpression expression)
        {
            Parent = parent;
            Expression = expression;
            _context = parent._context;
        }

        internal RouteDefinition PackageAndReturn()
        {
            Parent._steps.Add(new SplitExpressionStep(
                Expression, Steps.Count > 0 ? Steps : null,
                IsParallel, MaxDop, AggStrategy, StopOnEx, TimeoutValue));
            return Parent;
        }
    }

    private sealed class StreamingSplitFuncScope : RouteDefinition
    {
        internal readonly RouteDefinition Parent;
        internal readonly Func<IExchange, IAsyncEnumerable<object?>> Splitter;
        internal bool StopOnEx = true;

        internal StreamingSplitFuncScope(RouteDefinition parent, Func<IExchange, IAsyncEnumerable<object?>> splitter)
        {
            Parent = parent;
            Splitter = splitter;
            _context = parent._context;
        }

        internal RouteDefinition PackageAndReturn()
        {
            Parent._steps.Add(new StreamingSplitStep(
                Splitter, Steps.Count > 0 ? Steps : null, StopOnEx));
            return Parent;
        }
    }

    private sealed class OnExceptionScope : RouteDefinition, IExceptionConfig
    {
        internal readonly RouteDefinition Parent;
        public Type ExceptionType { get; }
        public int MaxRedeliveriesValue { get; set; }
        public TimeSpan? RedeliveryDelayValue { get; set; }
        public double BackoffMultiplierValue { get; set; } = 1.0;
        public bool UseExponentialBackoffValue { get; set; }
        public bool HandledValue { get; set; }
        public bool ContinuedValue { get; set; }
        public Func<IExchange, bool>? OnWhenPredicateValue { get; set; }
        public LogLevel RetryAttemptedLogLevelValue { get; set; } = LogLevel.Warning;
        public LogLevel RetriesExhaustedLogLevelValue { get; set; } = LogLevel.Error;
        public Action<IExchange>? OnExceptionOccurredCallback { get; set; }
        public Func<IExchange, bool>? RetryWhilePredicateValue { get; set; }
        public Action<IExchange>? OnRedeliveryCallbackValue { get; set; }
        public Action<IExchange>? OnPrepareFailureCallbackValue { get; set; }
        public bool UseOriginalMessageValue { get; set; }
        public bool UseOriginalBodyValue { get; set; }
        public bool AllowRedeliveryWhileStoppingValue { get; set; }
        public bool LogStackTraceValue { get; set; } = true;
        public bool LogExhaustedValue { get; set; } = true;

        internal OnExceptionScope(RouteDefinition parent, Type exceptionType)
        {
            Parent = parent;
            ExceptionType = exceptionType;
            _context = parent._context;
        }

        internal RouteDefinition PackageAndReturn()
        {
            var handler = new OnExceptionHandler(
                ExceptionType, Steps, MaxRedeliveriesValue,
                RedeliveryDelayValue, BackoffMultiplierValue, UseExponentialBackoffValue,
                HandledValue, ContinuedValue, OnWhenPredicateValue,
                RetryAttemptedLogLevelValue, RetriesExhaustedLogLevelValue, OnExceptionOccurredCallback,
                RetryWhilePredicateValue, OnRedeliveryCallbackValue, OnPrepareFailureCallbackValue,
                UseOriginalMessageValue, UseOriginalBodyValue, AllowRedeliveryWhileStoppingValue,
                LogStackTraceValue, LogExhaustedValue);
            Parent._steps.Add(new OnExceptionStep([handler]));
            return Parent;
        }
    }

    private sealed class MultiOnExceptionScope : RouteDefinition, IExceptionConfig
    {
        internal readonly RouteDefinition Parent;
        internal readonly Type[] ExceptionTypes;
        public Type ExceptionType => ExceptionTypes[0]; // primary type for IExceptionConfig
        public int MaxRedeliveriesValue { get; set; }
        public TimeSpan? RedeliveryDelayValue { get; set; }
        public double BackoffMultiplierValue { get; set; } = 1.0;
        public bool UseExponentialBackoffValue { get; set; }
        public bool HandledValue { get; set; }
        public bool ContinuedValue { get; set; }
        public Func<IExchange, bool>? OnWhenPredicateValue { get; set; }
        public LogLevel RetryAttemptedLogLevelValue { get; set; } = LogLevel.Warning;
        public LogLevel RetriesExhaustedLogLevelValue { get; set; } = LogLevel.Error;
        public Action<IExchange>? OnExceptionOccurredCallback { get; set; }
        public Func<IExchange, bool>? RetryWhilePredicateValue { get; set; }
        public Action<IExchange>? OnRedeliveryCallbackValue { get; set; }
        public Action<IExchange>? OnPrepareFailureCallbackValue { get; set; }
        public bool UseOriginalMessageValue { get; set; }
        public bool UseOriginalBodyValue { get; set; }
        public bool AllowRedeliveryWhileStoppingValue { get; set; }
        public bool LogStackTraceValue { get; set; } = true;
        public bool LogExhaustedValue { get; set; } = true;

        internal MultiOnExceptionScope(RouteDefinition parent, Type[] exceptionTypes)
        {
            Parent = parent;
            ExceptionTypes = exceptionTypes;
            _context = parent._context;
        }

        internal RouteDefinition PackageAndReturn()
        {
            var handlers = new List<OnExceptionHandler>(ExceptionTypes.Length);
            foreach (var exType in ExceptionTypes)
            {
                handlers.Add(new OnExceptionHandler(
                    exType, Steps, MaxRedeliveriesValue,
                    RedeliveryDelayValue, BackoffMultiplierValue, UseExponentialBackoffValue,
                    HandledValue, ContinuedValue, OnWhenPredicateValue,
                    RetryAttemptedLogLevelValue, RetriesExhaustedLogLevelValue, OnExceptionOccurredCallback,
                    RetryWhilePredicateValue, OnRedeliveryCallbackValue, OnPrepareFailureCallbackValue,
                    UseOriginalMessageValue, UseOriginalBodyValue, AllowRedeliveryWhileStoppingValue,
                    LogStackTraceValue, LogExhaustedValue));
            }
            Parent._steps.Add(new OnExceptionStep(handlers));
            return Parent;
        }
    }

    private sealed class LogScope : RouteDefinition
    {
        internal readonly RouteDefinition Parent;
        internal readonly LogLevel Level;
        internal readonly List<string> Messages = [];
        internal readonly List<Func<IExchange, string>> MessageFuncs = [];
        internal readonly List<string> HeaderNames = [];
        internal readonly List<string> PropertyNames = [];
        internal bool IncludeRouteId;

        internal LogScope(RouteDefinition parent, LogLevel level)
        {
            Parent = parent;
            Level = level;
            _context = parent._context;
        }

        internal RouteDefinition PackageAndReturn()
        {
            Parent._steps.Add(new RichLogStep(
                Messages, MessageFuncs, HeaderNames, PropertyNames, Level, IncludeRouteId));
            return Parent;
        }
    }

    private sealed class TracedScope : RouteDefinition
    {
        internal readonly RouteDefinition Parent;
        internal readonly string SpanName;

        internal TracedScope(RouteDefinition parent, string spanName)
        {
            Parent = parent;
            SpanName = spanName;
            _context = parent._context;
        }

        internal RouteDefinition PackageAndReturn()
        {
            Parent._steps.Add(new TracedStep(SpanName, Steps));
            return Parent;
        }
    }

    private sealed class MeteredScope : RouteDefinition
    {
        internal readonly RouteDefinition Parent;
        internal readonly string StepName;

        internal MeteredScope(RouteDefinition parent, string stepName)
        {
            Parent = parent;
            StepName = stepName;
            _context = parent._context;
        }

        internal RouteDefinition PackageAndReturn()
        {
            Parent._steps.Add(new MeteredStep(StepName, Steps));
            return Parent;
        }
    }

    private sealed class SagaScope : RouteDefinition
    {
        internal readonly RouteDefinition Parent;
        internal readonly SagaDefinition Definition = new();

        internal SagaScope(RouteDefinition parent)
        {
            Parent = parent;
            _context = parent._context;
        }

        internal RouteDefinition PackageAndReturn()
        {
            Parent._steps.Add(Definition.Build());
            return Parent;
        }
    }
}

#pragma warning disable CS0619
using System.Linq;
using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Expressions;
using redb.Route.Processors;
using redb.Route.Telemetry;
using redb.Route.Transactions;
using redb.Route.Validation;

namespace redb.Route.Definitions;

/// <summary>
/// New route definition using the canonical ProcessorDefinition model.
/// Each leaf method adds a child <see cref="ProcessorDefinition"/> to <see cref="ProcessorDefinition.Outputs"/>
/// and returns <c>this</c> for chaining.
/// Scope-opener methods return the newly created child definition; <c>End()</c> traverses
/// back to the parent via <see cref="IProcessorDefinition.Parent"/>.
/// <para>
/// <see cref="CreateProcessor"/> compiles all <see cref="ProcessorDefinition.Outputs"/> into
/// a sequential <see cref="PipelineProcessor"/>.
/// </para>
/// </summary>
public class RouteDefinition : ProcessorDefinition, IRouteDefinition
{
    private string? _routeId;
    private string? _fromUri;
    private bool _autoStart = true;
    private TimeSpan? _processingTimeout;
    private bool _cluster;
    private IRoutePolicy? _routePolicy;
    internal IRouteContext? _context;

    /// <summary>Route context captured during compile (null until the route is added to a context).</summary>
    public IRouteContext? Context
    {
        get
        {
            if (_context is not null) return _context;
            // Walk up the parent chain to find the root RouteDefinition (which carries the context).
            for (var p = Parent; p is not null; p = (p as ProcessorDefinition)?.Parent)
            {
                if (p is RouteDefinition rd && rd._context is not null) return rd._context;
            }
            return null;
        }
    }

    /// <summary>
    /// Reflects the route's compiled step tree as a flat sequence interleaving the live
    /// <see cref="IProcessorDefinition"/> nodes from <see cref="ProcessorDefinition.Outputs"/>
    /// with their canonical <see cref="RouteStep"/> projections (when one exists).
    /// </summary>
    public IEnumerable<object> Steps
    {
        get
        {
            foreach (var output in Outputs)
            {
                yield return output;
                var projected = RouteStepProjection.TryProject(output);
                if (projected is not null)
                    yield return projected;
            }
        }
    }

    /// <summary>Flag controlling whether the route id is shown in log/telemetry output (default false).</summary>
    public bool ShowRouteIdValue { get; internal set; }

    /// <summary>
    /// True when this route's compiled pipeline opens an explicit declarative
    /// <see cref="Transaction(TransactionPolicy?)"/> scope at the top level (Apache Camel parity).
    /// </summary>
    public bool IsTransacted => Outputs.Any(o => o is TransactionDefinition);

    // ── IProcessorDefinition.CreateProcessor ─────────────────────────────────

    /// <inheritdoc />
    public override IProcessor CreateProcessor(IRouteContext context)
    {
        if (Outputs.Count == 0)
            return new DelegateProcessor(_ => { });

        // Detect inline OnException blocks declared inside the route body. In Apache Camel
        // these are route-scoped — they wrap the entire route pipeline regardless of where
        // they appear textually. We hoist them out of the linear pipeline into a stack of
        // wrappers around the remaining body steps.
        var hasInlineOnException = false;
        for (var i = 0; i < Outputs.Count; i++)
        {
            if (Outputs[i] is OnExceptionDefinition) { hasInlineOnException = true; break; }
        }

        if (!hasInlineOnException)
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

        return CreateProcessorWithOnExceptionHoisting(context);
    }

    private IProcessor CreateProcessorWithOnExceptionHoisting(IRouteContext context)
    {
        var bodySteps = new List<IProcessor>();
        var onExceptions = new List<OnExceptionDefinition>();

        foreach (var output in Outputs)
        {
            if (output is OnExceptionDefinition oe)
            {
                onExceptions.Add(oe);
            }
            else
            {
                var p = output.CreateProcessor(context);
                if (p != null) bodySteps.Add(p);
            }
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

    // ── Route-level identity ──────────────────────────────────────────────────

    /// <inheritdoc />
    public IRouteDefinition RouteId(string routeId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(routeId);
        _routeId = routeId;
        return this;
    }

    /// <inheritdoc />
    public string? GetRouteId() => _routeId;

    /// <inheritdoc />
    public IRouteDefinition AutoStart(bool value = true) { _autoStart = value; return this; }

    /// <inheritdoc />
    public bool GetAutoStart() => _autoStart;

    /// <inheritdoc />
    public IRouteDefinition ProcessingTimeout(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero && timeout != System.Threading.Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(timeout), "Timeout must be positive or Infinite.");
        _processingTimeout = timeout;
        return this;
    }

    /// <summary>Gets the per-route processing timeout, or null if not set.</summary>
    public TimeSpan? GetProcessingTimeout() => _processingTimeout;

    // ── Source ───────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public IRouteDefinition From(string uri)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uri);
        _fromUri = uri;
        return this;
    }

    /// <inheritdoc />
    public string? GetFromUri() => _fromUri;

    // ── Destination ──────────────────────────────────────────────────────────

    /// <inheritdoc />
    public IRouteDefinition To(string uri)
    {
        AddOutput(new ToDefinition(uri));
        return this;
    }

    /// <inheritdoc />
    public IRouteDefinition ToD(string uriTemplate)
    {
        AddOutput(new ToDynamicDefinition(uriTemplate));
        return this;
    }

    /// <inheritdoc />
    public IRouteDefinition ToD(IExpression uriExpression)
    {
        AddOutput(new ToDynamicDefinition(uriExpression));
        return this;
    }

    /// <inheritdoc />
    public IRouteDefinition ToD(Func<IExchange, string> uriFactory)
    {
        AddOutput(new ToDynamicDefinition(uriFactory));
        return this;
    }

    // ── Processing delegates ──────────────────────────────────────────────────

    /// <inheritdoc />
    public IRouteDefinition Process(Action<IExchange> action)
    {
        AddOutput(new ProcessActionDefinition(action));
        return this;
    }

    /// <inheritdoc />
    public IRouteDefinition Process(Func<IExchange, CancellationToken, Task> action)
    {
        AddOutput(new ProcessAsyncDefinition(action));
        return this;
    }

    /// <inheritdoc />
    public IRouteDefinition Process(IProcessor processor)
    {
        AddOutput(new ProcessInstanceDefinition(processor));
        return this;
    }

    // ── Transform / body ─────────────────────────────────────────────────────

    /// <inheritdoc />
    public IRouteDefinition SetBody(object? value)
    {
        AddOutput(new SetBodyStaticDefinition(value));
        return this;
    }

    /// <inheritdoc />
    public IRouteDefinition SetBody(Func<IExchange, object?> factory)
    {
        AddOutput(new SetBodyFactoryDefinition(factory));
        return this;
    }

    /// <inheritdoc />
    public IRouteDefinition SetBody(IExpression expression)
    {
        AddOutput(new SetBodyExpressionDefinition(expression));
        return this;
    }

    /// <inheritdoc />
    public IRouteDefinition SetBodyExpression(string template)
    {
        AddOutput(new SetBodyStringExpressionDefinition(template));
        return this;
    }

    /// <inheritdoc />
    public IRouteDefinition Transform(Func<IExchange, object?> transform)
    {
        AddOutput(new TransformDefinition(transform));
        return this;
    }

    /// <inheritdoc />
    public IRouteDefinition Transform(IExpression expression)
    {
        AddOutput(new TransformExpressionDefinition(expression));
        return this;
    }

    /// <inheritdoc />
    public IRouteDefinition RemoveBody()
    {
        AddOutput(new RemoveBodyDefinition());
        return this;
    }

    // ── Headers ──────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public IRouteDefinition SetHeader(string key, object? value)
    {
        AddOutput(new SetHeaderStaticDefinition(key, value));
        return this;
    }

    /// <inheritdoc />
    public IRouteDefinition SetHeader(string key, Func<IExchange, object?> factory)
    {
        AddOutput(new SetHeaderFactoryDefinition(key, factory));
        return this;
    }

    /// <inheritdoc />
    public IRouteDefinition SetHeader(string name, IExpression expression)
    {
        AddOutput(new SetHeaderExpressionDefinition(name, expression));
        return this;
    }

    /// <inheritdoc />
    public IRouteDefinition SetHeaderExpression(string name, string template)
    {
        AddOutput(new SetHeaderStringExpressionDefinition(name, template));
        return this;
    }

    /// <inheritdoc />
    public IRouteDefinition RemoveHeader(string key)
    {
        AddOutput(new RemoveHeaderDefinition(key));
        return this;
    }

    // ── Properties ───────────────────────────────────────────────────────────

    /// <inheritdoc />
    public IRouteDefinition SetProperty(string key, object? value)
    {
        AddOutput(new SetPropertyStaticDefinition(key, value));
        return this;
    }

    /// <inheritdoc />
    public IRouteDefinition SetProperty(string key, Func<IExchange, object?> factory)
    {
        AddOutput(new SetPropertyFactoryDefinition(key, factory));
        return this;
    }

    /// <inheritdoc />
    public IRouteDefinition SetProperty(string key, IExpression expression)
    {
        AddOutput(new SetPropertyExpressionDefinition(key, expression));
        return this;
    }

    /// <inheritdoc />
    public IRouteDefinition SetPropertyExpression(string key, string template)
    {
        AddOutput(new SetPropertyStringExpressionDefinition(key, template));
        return this;
    }

    /// <inheritdoc />
    public IRouteDefinition RemoveProperty(string key)
    {
        AddOutput(new RemovePropertyDefinition(key));
        return this;
    }

    // ── Logging ──────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public IRouteDefinition Log(string message, LogLevel level = LogLevel.Information)
    {
        AddOutput(new LogStaticDefinition(message, level));
        return this;
    }

    /// <inheritdoc />
    public IRouteDefinition Log(Func<IExchange, string> messageFactory, LogLevel level = LogLevel.Information)
    {
        AddOutput(new LogDynamicDefinition(messageFactory, level));
        return this;
    }

    // ── Stop / Throw ──────────────────────────────────────────────────────────

    /// <inheritdoc />
    public IRouteDefinition Stop()
    {
        AddOutput(new StopDefinition());
        return this;
    }

    /// <inheritdoc />
    public IRouteDefinition ThrowException()
    {
        AddOutput(new RethrowExceptionDefinition());
        return this;
    }

    /// <inheritdoc />
    public IRouteDefinition ThrowException(string message)
    {
        AddOutput(new ThrowMessageDefinition(message));
        return this;
    }

    /// <inheritdoc />
    public IRouteDefinition ThrowException(Exception exception)
    {
        AddOutput(new ThrowExceptionDefinition(exception));
        return this;
    }

    /// <inheritdoc />
    public IRouteDefinition ThrowException(Type exceptionType, string message)
    {
        AddOutput(new ThrowExceptionTypeDefinition(exceptionType, message));
        return this;
    }

    /// <inheritdoc />
    public IRouteDefinition ThrowException<TException>(string? message = null) where TException : Exception, new()
    {
        AddOutput(new ThrowExceptionTypeDefinition(typeof(TException), message));
        return this;
    }

    // ── Delay / Sampling ─────────────────────────────────────────────────────

    /// <inheritdoc />
    public IRouteDefinition Delay(TimeSpan duration)
    {
        AddOutput(new DelayDefinition(duration));
        return this;
    }

    /// <inheritdoc />
    public IRouteDefinition Delay(Func<IExchange, TimeSpan> factory)
    {
        AddOutput(new DelayFactoryDefinition(factory));
        return this;
    }

    /// <inheritdoc />
    public IRouteDefinition Sample(long messageFrequency)
    {
        AddOutput(new SampleCountDefinition(messageFrequency));
        return this;
    }

    /// <inheritdoc />
    public IRouteDefinition Sample(TimeSpan period)
    {
        AddOutput(new SamplePeriodDefinition(period));
        return this;
    }

    // ── Stream caching ───────────────────────────────────────────────────────

    /// <inheritdoc />
    public IRouteDefinition StreamCaching(long? spoolThreshold = null)
    {
        AddOutput(new StreamCachingDefinition(spoolThreshold));
        return this;
    }

    // ── Validation ───────────────────────────────────────────────────────────

    /// <inheritdoc />
    public IRouteDefinition Validate(IMessageValidator validator, bool throwOnFailure = true)
    {
        AddOutput(new ValidateInstanceDefinition(validator, throwOnFailure));
        return this;
    }

    /// <inheritdoc />
    public IRouteDefinition Validate(Func<IExchange, bool> predicate, string errorMessage = "Validation failed", bool throwOnFailure = true)
    {
        AddOutput(new ValidatePredicateDefinition(predicate, errorMessage, throwOnFailure));
        return this;
    }

    /// <inheritdoc />
    public IRouteDefinition ValidateJsonSchema(string schemaJson, bool throwOnFailure = true)
    {
        AddOutput(new ValidateJsonSchemaStringDefinition(schemaJson, throwOnFailure));
        return this;
    }

    /// <inheritdoc />
    public IRouteDefinition ValidateJsonSchema(Json.Schema.JsonSchema schema, bool throwOnFailure = true)
    {
        AddOutput(new ValidateJsonSchemaObjectDefinition(schema, throwOnFailure));
        return this;
    }

    /// <inheritdoc />
    public IRouteDefinition ValidateXsd(string xsdContent, bool throwOnFailure = true)
    {
        AddOutput(new ValidateXsdStringDefinition(xsdContent, throwOnFailure));
        return this;
    }

    /// <inheritdoc />
    public IRouteDefinition ValidateXsd(string? targetNamespace, string xsdContent, bool throwOnFailure = true)
    {
        AddOutput(new ValidateXsdNamespaceDefinition(targetNamespace, xsdContent, throwOnFailure));
        return this;
    }

    /// <inheritdoc />
    public IRouteDefinition ValidateXsd(System.Xml.Schema.XmlSchemaSet schemaSet, bool throwOnFailure = true)
    {
        AddOutput(new ValidateXsdSchemaSetDefinition(schemaSet, throwOnFailure));
        return this;
    }

    // ── Serialization ────────────────────────────────────────────────────────

    /// <inheritdoc />
    public IRouteDefinition Marshal(Type serializerType)
    {
        AddOutput(new MarshalDefinition(serializerType));
        return this;
    }

    /// <inheritdoc />
    public IRouteDefinition Marshal<TSerializer>() where TSerializer : class
    {
        AddOutput(new MarshalDefinition(typeof(TSerializer)));
        return this;
    }

    /// <inheritdoc />
    public IRouteDefinition Unmarshal(Type serializerType, Type targetType)
    {
        AddOutput(new UnmarshalDefinition(serializerType, targetType));
        return this;
    }

    /// <inheritdoc />
    public IRouteDefinition Unmarshal<TSerializer, TTarget>() where TSerializer : class
    {
        AddOutput(new UnmarshalDefinition(typeof(TSerializer), typeof(TTarget)));
        return this;
    }

    /// <summary>
    /// Apache Camel parity: unmarshal the body to <typeparamref name="T"/> using the
    /// <see cref="IDataFormatRegistry"/> entry resolved from the incoming message's ContentType.
    /// </summary>
    public IRouteDefinition Unmarshal<T>()
    {
        AddOutput(new ConvertBodyDefinition(typeof(T)));
        return this;
    }

    /// <summary>Apache Camel parity: convert the exchange body to <typeparamref name="T"/>.</summary>
    public IRouteDefinition ConvertBody<T>()
    {
        AddOutput(new ConvertBodyDefinition(typeof(T)));
        return this;
    }

    /// <summary>Apache Camel parity: convert the exchange body to <paramref name="targetType"/>.</summary>
    public IRouteDefinition ConvertBody(Type targetType)
    {
        AddOutput(new ConvertBodyDefinition(targetType));
        return this;
    }

    /// <summary>
    /// Apache Camel parity: open a typed scope. Auto-converts the body to <typeparamref name="T"/>
    /// and exposes typed <c>Process</c>/<c>Filter</c>/<c>Transform</c> overloads to its children.
    /// </summary>
    public OfTypeDefinition<T> OfType<T>()
    {
        var def = new OfTypeDefinition<T>();
        AddOutput(def);
        return def;
    }

    // ── Transactions ─────────────────────────────────────────────────────────

    /// <inheritdoc />
    public IRouteDefinition BeginTransaction()
    {
        AddOutput(new BeginTransactionDefinition());
        return this;
    }

    /// <inheritdoc />
    public IRouteDefinition BeginTransaction(Transactions.TransactionPolicy policy)
    {
        AddOutput(new BeginTransactionDefinition(policy));
        return this;
    }

    /// <inheritdoc />
    public IRouteDefinition CommitTransaction()
    {
        AddOutput(new CommitTransactionDefinition());
        return this;
    }

    /// <inheritdoc />
    public IRouteDefinition RollbackTransaction()
    {
        AddOutput(new RollbackTransactionDefinition());
        return this;
    }

    /// <inheritdoc />
    public TransactionDefinition Transaction(TransactionPolicy? policy = null)
    {
        var def = new TransactionDefinition(policy);
        AddOutput(def);
        return def;
    }

    // ── Telemetry ────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public TracedDefinition Traced(string operationName)
    {
        var def = new TracedDefinition(operationName);
        AddOutput(def);
        return def;
    }

    /// <inheritdoc />
    public IRouteDefinition Traced(string operationName, Action<IExchange> action)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationName);
        AddOutput(new ProcessInstanceDefinition(
            new Telemetry.InstrumentedProcessor(new Processors.DelegateProcessor(action), operationName)));
        return this;
    }

    /// <inheritdoc />
    public IRouteDefinition Traced(string operationName, Func<IExchange, CancellationToken, Task> action)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationName);
        ArgumentNullException.ThrowIfNull(action);
        AddOutput(new ProcessInstanceDefinition(
            new Telemetry.InstrumentedProcessor(new Processors.DelegateProcessor(action), operationName)));
        return this;
    }

    /// <inheritdoc />
    public IRouteDefinition Traced(string operationName, IProcessor processor)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationName);
        ArgumentNullException.ThrowIfNull(processor);
        AddOutput(new ProcessInstanceDefinition(
            new Telemetry.InstrumentedProcessor(processor, operationName)));
        return this;
    }

    /// <inheritdoc />
    public MeteredDefinition Metered(string stepName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stepName);
        if (stepName.Contains("${"))
            throw new ArgumentException(
                $"Metered() step name must be a constant to avoid metric cardinality explosion (got dynamic template '{stepName}'). " +
                "Use tags on the activity instead of templating the name.",
                nameof(stepName));
        var def = new MeteredDefinition(stepName);
        AddOutput(def);
        return def;
    }

    /// <inheritdoc />
    public IRouteDefinition Metered(string stepName, Action<IExchange> action)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stepName);
        ArgumentNullException.ThrowIfNull(action);
        if (stepName.Contains("${"))
            throw new ArgumentException(
                $"Metered() step name must be a constant to avoid metric cardinality explosion (got dynamic template '{stepName}').",
                nameof(stepName));
        AddOutput(new ProcessInstanceDefinition(
            new Telemetry.MeteredStepProcessor(new Processors.DelegateProcessor(action), stepName)));
        return this;
    }

    /// <inheritdoc />
    public IRouteDefinition Metered(string stepName, Func<IExchange, CancellationToken, Task> action)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stepName);
        ArgumentNullException.ThrowIfNull(action);
        AddOutput(new ProcessInstanceDefinition(
            new Telemetry.MeteredStepProcessor(new Processors.DelegateProcessor(action), stepName)));
        return this;
    }

    /// <inheritdoc />
    public IRouteDefinition Metered(string stepName, IProcessor processor)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stepName);
        ArgumentNullException.ThrowIfNull(processor);
        AddOutput(new ProcessInstanceDefinition(
            new Telemetry.MeteredStepProcessor(processor, stepName)));
        return this;
    }

    // ── Enrichment ───────────────────────────────────────────────────────────

    /// <inheritdoc />
    public IRouteDefinition WireTap(string uri)
    {
        AddOutput(new WireTapDefinition(uri));
        return this;
    }

    /// <inheritdoc />
    public IRouteDefinition WireTap(string uri, Action<IExchange> onPrepare)
    {
        AddOutput(new WireTapDefinition(uri, onPrepare: onPrepare));
        return this;
    }

    /// <inheritdoc />
    public IRouteDefinition WireTap(string uri, Func<IExchange, object?> newBodyFactory)
    {
        AddOutput(new WireTapDefinition(uri, newBodyFactory: newBodyFactory));
        return this;
    }

    /// <inheritdoc />
    public IRouteDefinition WireTap(string uri, Action<IExchange> onPrepare, Func<IExchange, object?> newBodyFactory)
    {
        AddOutput(new WireTapDefinition(uri, onPrepare, newBodyFactory));
        return this;
    }

    /// <inheritdoc />
    public IRouteDefinition WireTap(Func<IExchange, string> uriFactory)
    {
        AddOutput(new WireTapDynamicDefinition(uriFactory));
        return this;
    }

    /// <inheritdoc />
    public IRouteDefinition WireTap(Func<IExchange, string> uriFactory, Action<IExchange> onPrepare)
    {
        AddOutput(new WireTapDynamicDefinition(uriFactory, onPrepare: onPrepare));
        return this;
    }

    /// <inheritdoc />
    public IRouteDefinition WireTap(Func<IExchange, string> uriFactory, Func<IExchange, object?> newBodyFactory)
    {
        AddOutput(new WireTapDynamicDefinition(uriFactory, newBodyFactory: newBodyFactory));
        return this;
    }

    /// <inheritdoc />
    public IRouteDefinition WireTap(Func<IExchange, string> uriFactory, Action<IExchange> onPrepare, Func<IExchange, object?> newBodyFactory)
    {
        AddOutput(new WireTapDynamicDefinition(uriFactory, onPrepare, newBodyFactory));
        return this;
    }

    /// <inheritdoc />
    public IRouteDefinition Enrich(string resourceUri, Func<IExchange, IExchange, IExchange> mergeStrategy)
    {
        AddOutput(new EnrichDefinition(resourceUri, mergeStrategy));
        return this;
    }

    /// <inheritdoc />
    public IRouteDefinition Enrich(Func<IExchange, string> uriFactory, Func<IExchange, IExchange, IExchange> mergeStrategy)
    {
        AddOutput(new EnrichDynamicDefinition(uriFactory, mergeStrategy));
        return this;
    }

    /// <summary>Apache Camel parity: poll an external endpoint and merge the polled result.</summary>
    public IRouteDefinition PollEnrich(
        string resourceUri,
        Func<IExchange, IExchange?, IExchange> mergeStrategy,
        TimeSpan? timeout = null)
    {
        AddOutput(new PollEnrichDefinition(resourceUri, mergeStrategy, timeout));
        return this;
    }

    /// <inheritdoc />
    public IRouteDefinition PollEnrich(
        Func<IExchange, string> uriFactory,
        Func<IExchange, IExchange?, IExchange> mergeStrategy,
        TimeSpan? timeout = null)
    {
        AddOutput(new PollEnrichDynamicDefinition(uriFactory, mergeStrategy, timeout));
        return this;
    }

    /// <summary>Apache Camel parity: route to a list of recipient URIs computed at runtime.</summary>
    public IRouteDefinition RecipientList(
        Func<IExchange, IEnumerable<string>> recipientListFactory,
        bool parallelProcessing = false,
        bool stopOnException = false,
        Func<IExchange, IExchange, IExchange>? aggregationStrategy = null)
    {
        AddOutput(new RecipientListDefinition(
            recipientListFactory, parallelProcessing, stopOnException, aggregationStrategy));
        return this;
    }

    /// <summary>Apache Camel parity: iteratively route to URIs returned by a routing function.</summary>
    public IRouteDefinition DynamicRouter(Func<IExchange, string?> routingFunction)
    {
        AddOutput(new DynamicRouterDefinition(routingFunction));
        return this;
    }

    /// <summary>
    /// Apache Camel parity: open a Resequencer scope. The contained child outputs receive
    /// exchanges in the order determined by <paramref name="keySelector"/>.
    /// </summary>
    public ResequenceDefinition Resequence(
        Func<IExchange, long> keySelector,
        int batchSize = 100,
        TimeSpan? timeout = null)
    {
        var def = new ResequenceDefinition(keySelector, batchSize, timeout);
        AddOutput(def);
        return def;
    }

    // ── Scope openers ────────────────────────────────────────────────────────

    /// <inheritdoc />
    public FilterDefinition Filter(Func<IExchange, bool> predicate)
    {
        var def = new FilterDefinition(predicate);
        AddOutput(def);
        return def;
    }

    /// <inheritdoc />
    public FilterDefinition Filter(IExpression expression)
    {
        ArgumentNullException.ThrowIfNull(expression);
        var def = new FilterDefinition(e => ConvertToBoolean(expression.Evaluate<object?>(e)))
        {
            SourceExpression = expression,
        };
        AddOutput(def);
        return def;
    }

    /// <inheritdoc />
    public IdempotentConsumerDefinition IdempotentConsumer(
        IIdempotentRepository repository,
        Func<IExchange, string> keyExtractor,
        bool skipDuplicate = true)
    {
        var def = new IdempotentConsumerDefinition(repository, keyExtractor, skipDuplicate);
        AddOutput(def);
        return def;
    }

    /// <summary>
    /// Apache Camel parity overload that looks up the repository by registered name from the
    /// route context's <see cref="redb.Route.Components.IdempotentRepositoryRegistryExtensions.GetIdempotentRepositoryProvider"/>.
    /// </summary>
    public IdempotentConsumerDefinition IdempotentConsumer(
        Func<IExchange, string> keyExtractor,
        string repositoryName,
        bool skipDuplicate = true)
    {
        var def = new IdempotentConsumerDefinition(repositoryName, keyExtractor, skipDuplicate);
        AddOutput(def);
        return def;
    }

    /// <inheritdoc />
    public ChoiceDefinition Choice()
    {
        var def = new ChoiceDefinition();
        AddOutput(def);
        return def;
    }

    /// <inheritdoc />
    public SplitDefinition Split(Func<IExchange, IEnumerable<object?>> splitter)
    {
        var def = new SplitDefinition(splitter);
        AddOutput(def);
        return def;
    }

    /// <inheritdoc />
    public SplitDefinition Split(IExpression expression)
    {
        var def = new SplitDefinition(expression);
        AddOutput(def);
        return def;
    }

    /// <inheritdoc />
    public SplitDefinition Split(Func<IExchange, IAsyncEnumerable<object?>> splitter)
    {
        var def = new SplitDefinition(splitter);
        AddOutput(def);
        return def;
    }

    /// <inheritdoc />
    public MulticastDefinition Multicast()
    {
        var def = new MulticastDefinition();
        AddOutput(def);
        return def;
    }

    /// <inheritdoc />
    public TryCatchDefinition TryCatch()
    {
        var def = new TryCatchDefinition();
        AddOutput(def);
        return def;
    }

    /// <inheritdoc />
    public AggregateDefinition Aggregate(
        Func<IExchange, string> correlationKey,
        Func<IExchange, IExchange, IExchange> aggregationStrategy,
        Func<IExchange, bool> completionPredicate)
    {
        var def = new AggregateDefinition(correlationKey, aggregationStrategy, completionPredicate);
        AddOutput(def);
        return def;
    }

    /// <inheritdoc />
    public ThrottleDefinition Throttle(int maxPerPeriod)
    {
        var def = new ThrottleDefinition(maxPerPeriod);
        AddOutput(def);
        return def;
    }

    /// <inheritdoc />
    public KeyedThrottleDefinition Throttle(
        Func<IExchange, string> keyExtractor,
        int maxPerPeriod,
        TimeSpan? period = null)
    {
        var def = new KeyedThrottleDefinition(keyExtractor, maxPerPeriod, period);
        AddOutput(def);
        return def;
    }

    /// <inheritdoc />
    public CircuitBreakerDefinition CircuitBreaker()
    {
        var def = new CircuitBreakerDefinition();
        AddOutput(def);
        return def;
    }

    /// <inheritdoc />
    public DebounceDefinition Debounce(Func<IExchange, string> keyExtractor, TimeSpan quietPeriod)
    {
        var def = new DebounceDefinition(keyExtractor, quietPeriod);
        AddOutput(def);
        return def;
    }

    /// <inheritdoc />
    public LoopDefinition Loop(int count, bool copy = false, bool shareScope = true)
    {
        var def = new LoopDefinition(count, copy, shareScope);
        AddOutput(def);
        return def;
    }

    /// <inheritdoc />
    public LoopDefinition Loop(Func<IExchange, bool> condition, bool copy = false, bool shareScope = true)
    {
        var def = new LoopDefinition(condition, copy, shareScope);
        AddOutput(def);
        return def;
    }

    /// <inheritdoc />
    public LoopDefinition Loop(Func<IExchange, int> countFactory, bool copy = false, bool shareScope = true)
    {
        var def = new LoopDefinition(countFactory, copy, shareScope);
        AddOutput(def);
        return def;
    }

    // ── Bean / Service Activator ──────────────────────────────────────────────

    /// <inheritdoc />
    public IRouteDefinition Bean<TService>(Func<TService, IExchange, CancellationToken, Task> method)
    {
        ArgumentNullException.ThrowIfNull(method);
        AddOutput(new BeanDefinition(
            typeof(TService),
            (svc, exchange, ct) => method((TService)svc, exchange, ct)));
        return this;
    }

    /// <inheritdoc />
    public IRouteDefinition Bean<TService>(Func<TService, IExchange, Task> method)
    {
        ArgumentNullException.ThrowIfNull(method);
        AddOutput(new BeanDefinition(
            typeof(TService),
            (svc, exchange, _) => method((TService)svc, exchange)));
        return this;
    }

    /// <inheritdoc />
    public IRouteDefinition Bean<TService>(Action<TService, IExchange> method)
    {
        ArgumentNullException.ThrowIfNull(method);
        AddOutput(new BeanDefinition(
            typeof(TService),
            (svc, exchange, _) => { method((TService)svc, exchange); return Task.CompletedTask; }));
        return this;
    }

    // ── Scatter-Gather ────────────────────────────────────────────────────────

    /// <inheritdoc />
    public IRouteDefinition ScatterGather(
        Func<IExchange, IExchange, IExchange> aggregationStrategy,
        params string[] recipients)
    {
        ArgumentNullException.ThrowIfNull(aggregationStrategy);
        if (recipients is null || recipients.Length == 0)
            throw new ArgumentException("At least one recipient is required.", nameof(recipients));

        var def = new ScatterGatherDefinition();
        ((IScatterGatherDefinition)def).Recipients(recipients);
        ((IScatterGatherDefinition)def).AggregationStrategy(aggregationStrategy);
        AddOutput(def);
        return this;
    }

    /// <inheritdoc />
    public IRouteDefinition ScatterGather(Action<IScatterGatherDefinition> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var def = new ScatterGatherDefinition();
        configure(def);
        if (def.AggregationStrategy is null)
            throw new InvalidOperationException("AggregationStrategy is required for ScatterGather.");
        if (def.StaticRecipients is null && def.DynamicRecipients is null)
            throw new InvalidOperationException("Recipients are required for ScatterGather.");
        AddOutput(def);
        return this;
    }

    // ── Saga ──────────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public IRouteDefinition Saga(Action<ISagaDefinition> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var def = new SagaDefinition();
        configure(def);
        if (def.Entries.Count == 0)
            throw new InvalidOperationException("Saga must have at least one step.");
        AddOutput(def);
        return this;
    }

    /// <inheritdoc />
    public SagaDefinition Saga()
    {
        var def = new SagaDefinition();
        def.SetParent(this);
        AddOutput(def);
        return def;
    }

    // ── LoadBalance ───────────────────────────────────────────────────────────

    /// <inheritdoc />
    public IRouteDefinition LoadBalance(ILoadBalancerStrategy strategy, params string[] uris)
    {
        ArgumentNullException.ThrowIfNull(strategy);
        if (uris is null || uris.Length == 0)
            throw new ArgumentException("At least one endpoint URI is required.", nameof(uris));

        var def = new LoadBalancerDefinition();
        ((ILoadBalancerDefinition)def).Endpoints(uris);
        ((ILoadBalancerDefinition)def).Strategy(strategy);
        AddOutput(def);
        return this;
    }

    /// <inheritdoc />
    public IRouteDefinition LoadBalance(Action<ILoadBalancerDefinition> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var def = new LoadBalancerDefinition();
        configure(def);
        if (def.Strategy is null)
            throw new InvalidOperationException(
                "Strategy is required for LoadBalance. Use UseRoundRobin(), UseFailover(), etc.");
        if (def.Endpoints is null || def.Endpoints.Length == 0)
            throw new InvalidOperationException("At least one endpoint is required for LoadBalance.");
        AddOutput(def);
        return this;
    }

    // ── Normalizer ────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public IRouteDefinition Normalize(Action<INormalizerDefinition> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var def = new NormalizerDefinition();
        configure(def);
        if (def.IsEmpty)
            throw new InvalidOperationException("Normalizer requires at least one When clause.");
        AddOutput(def);
        return this;
    }

    // ── Exception handling ────────────────────────────────────────────────────

    /// <inheritdoc />
    public IRouteDefinition ExceptionHandled()
    {
        AddOutput(new ExceptionHandledDefinition());
        return this;
    }

    /// <inheritdoc />
    public IRouteDefinition RollbackAll()
    {
        AddOutput(new RollbackAllDefinition());
        return this;
    }

    /// <inheritdoc />
    public OnExceptionDefinition OnException<TException>() where TException : Exception
        => OnException(typeof(TException));

    /// <inheritdoc />
    public OnExceptionDefinition OnException(params Type[] exceptionTypes)
    {
        if (exceptionTypes is null || exceptionTypes.Length == 0)
            throw new ArgumentException("At least one exception type is required.", nameof(exceptionTypes));

        foreach (var type in exceptionTypes)
        {
            if (!typeof(Exception).IsAssignableFrom(type))
                throw new ArgumentException(
                    $"{type.Name} is not an Exception type.", nameof(exceptionTypes));
        }

        var def = new OnExceptionDefinition(exceptionTypes);
        AddOutput(def);
        return def;
    }

    // ── Route-level policy ────────────────────────────────────────────────────

    /// <inheritdoc />
    public IRouteDefinition Cluster(bool value = true)
    {
        _cluster = value;
        return this;
    }

    /// <inheritdoc />
    public bool GetCluster() => _cluster;

    /// <inheritdoc />
    public IRouteDefinition RoutePolicy(IRoutePolicy policy)
    {
        _routePolicy = policy ?? throw new ArgumentNullException(nameof(policy));
        return this;
    }

    /// <inheritdoc />
    public IRoutePolicy? GetRoutePolicy() => _routePolicy;

    // ── Rich logging scope ────────────────────────────────────────────────────

    /// <inheritdoc />
    public RichLogScopeDefinition Log(LogLevel level)
    {
        var def = new RichLogScopeDefinition(level);
        AddOutput(def);
        return def;
    }

    private static bool ConvertToBoolean(object? value) => value switch
    {
        bool b => b,
        string s => bool.TryParse(s, out var result) ? result : !string.IsNullOrEmpty(s),
        null => false,
        _ => true
    };
}

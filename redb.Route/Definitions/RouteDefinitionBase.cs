#pragma warning disable CS0619
using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Expressions;
using redb.Route.Processors;
using redb.Route.Telemetry;
using redb.Route.Transactions;
using redb.Route.Validation;
using redb.Route.Xslt;

namespace redb.Route.Definitions;

/// <summary>
/// Generic CRTP base for every route / scope definition. All leaf-DSL methods are declared here
/// exactly once and return <typeparamref name="TSelf"/> for fluent chaining inside the concrete
/// scope. The class implements <see cref="IRouteDefinition"/>; covariant return types let the
/// typed leaf-DSL satisfy the interface contract whose declared return type is
/// <see cref="IRouteDefinition"/>.
/// <para>
/// Apache Camel parity: this mirrors <c>ProcessorDefinition&lt;Type extends ProcessorDefinition&lt;Type&gt;&gt;</c>
/// from camel-core-model. Scope-opener methods (<c>Filter</c>, <c>Choice</c>, <c>Split</c>, ...)
/// return concrete scope types; child <see cref="ProcessorDefinition.Outputs"/> are populated
/// via the inherited typed DSL.
/// </para>
/// </summary>
public abstract partial class RouteDefinitionBase<TSelf> : ProcessorDefinition, IRouteDefinition
    where TSelf : RouteDefinitionBase<TSelf>
{
    /// <summary>Typed self-reference for fluent chaining; safe because <typeparamref name="TSelf"/> is constrained to this class.</summary>
    protected TSelf Self => (TSelf)(object)this;

    // ── Route-level state (carried by the root RouteDefinition; scope instances inherit the
    //    fields but only the root reads them) ───────────────────────────────────────────────
    private string? _routeId;
    private string? _fromUri;
    private bool _autoStart = true;
    private TimeSpan? _processingTimeout;
    private bool _cluster;
    private bool? _messageHistory;
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

    /// <summary>Flag controlling whether the route id is shown in log/telemetry output (default false).</summary>
    public bool ShowRouteIdValue { get; internal set; }

    /// <summary>
    /// True when this route's compiled pipeline opens an explicit declarative
    /// <see cref="Transaction(TransactionPolicy?)"/> scope at the top level (Apache Camel parity).
    /// </summary>
    public bool IsTransacted => Outputs.Any(o => o is TransactionDefinition);

    // ── Route-level identity ──────────────────────────────────────────────────

    /// <inheritdoc />
    public TSelf RouteId(string routeId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(routeId);
        _routeId = routeId;
        return Self;
    }

    /// <inheritdoc />
    public string? GetRouteId() => _routeId;

    /// <inheritdoc />
    public TSelf AutoStart(bool value = true) { _autoStart = value; return Self; }

    /// <inheritdoc />
    public bool GetAutoStart() => _autoStart;

    /// <inheritdoc />
    public TSelf ProcessingTimeout(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero && timeout != System.Threading.Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(timeout), "Timeout must be positive or Infinite.");
        _processingTimeout = timeout;
        return Self;
    }

    /// <summary>Gets the per-route processing timeout, or null if not set.</summary>
    public TimeSpan? GetProcessingTimeout() => _processingTimeout;

    // ── Source ───────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public TSelf From(string uri)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uri);
        _fromUri = uri;
        return Self;
    }

    /// <inheritdoc />
    public string? GetFromUri() => _fromUri;

    // ── Destination ──────────────────────────────────────────────────────────

    /// <inheritdoc />
    public TSelf To(string uri)
    {
        AddOutput(new ToDefinition(uri));
        return Self;
    }

    /// <inheritdoc />
    public TSelf ToD(string uriTemplate)
    {
        AddOutput(new ToDynamicDefinition(uriTemplate));
        return Self;
    }

    /// <inheritdoc />
    public TSelf ToD(IExpression uriExpression)
    {
        AddOutput(new ToDynamicDefinition(uriExpression));
        return Self;
    }

    /// <inheritdoc />
    public TSelf ToD(Func<IExchange, string> uriFactory)
    {
        AddOutput(new ToDynamicDefinition(uriFactory));
        return Self;
    }

    // ── Processing delegates ──────────────────────────────────────────────────

    /// <inheritdoc />
    public TSelf Process(Action<IExchange> action)
    {
        AddOutput(new ProcessActionDefinition(action));
        return Self;
    }

    /// <inheritdoc />
    public TSelf Process(Func<IExchange, CancellationToken, Task> action)
    {
        AddOutput(new ProcessAsyncDefinition(action));
        return Self;
    }

    /// <inheritdoc />
    public TSelf Process(IProcessor processor)
    {
        AddOutput(new ProcessInstanceDefinition(processor));
        return Self;
    }

    // ── Transform / body ─────────────────────────────────────────────────────

    /// <inheritdoc />
    public TSelf SetBody(object? value)
    {
        AddOutput(new SetBodyStaticDefinition(value));
        return Self;
    }

    /// <inheritdoc />
    public TSelf SetBody(Func<IExchange, object?> factory)
    {
        AddOutput(new SetBodyFactoryDefinition(factory));
        return Self;
    }

    /// <inheritdoc />
    public TSelf SetBody(IExpression expression)
    {
        AddOutput(new SetBodyExpressionDefinition(expression));
        return Self;
    }

    /// <inheritdoc />
    public TSelf SetBodyExpression(string template)
    {
        AddOutput(new SetBodyStringExpressionDefinition(template));
        return Self;
    }

    /// <inheritdoc />
    public TSelf Transform(Func<IExchange, object?> transform)
    {
        AddOutput(new TransformDefinition(transform));
        return Self;
    }

    /// <inheritdoc />
    public TSelf Transform(IExpression expression)
    {
        AddOutput(new TransformExpressionDefinition(expression));
        return Self;
    }

    /// <inheritdoc />
    public TSelf RemoveBody()
    {
        AddOutput(new RemoveBodyDefinition());
        return Self;
    }

    // ── Headers ──────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public TSelf SetHeader(string key, object? value)
    {
        AddOutput(new SetHeaderStaticDefinition(key, value));
        return Self;
    }

    /// <inheritdoc />
    public TSelf SetHeader(string key, Func<IExchange, object?> factory)
    {
        AddOutput(new SetHeaderFactoryDefinition(key, factory));
        return Self;
    }

    /// <inheritdoc />
    public TSelf SetHeader(string name, IExpression expression)
    {
        AddOutput(new SetHeaderExpressionDefinition(name, expression));
        return Self;
    }

    /// <inheritdoc />
    public TSelf SetHeaderExpression(string name, string template)
    {
        AddOutput(new SetHeaderStringExpressionDefinition(name, template));
        return Self;
    }

    /// <inheritdoc />
    public TSelf RemoveHeader(string key)
    {
        AddOutput(new RemoveHeaderDefinition(key));
        return Self;
    }

    // ── Properties ───────────────────────────────────────────────────────────

    /// <inheritdoc />
    public TSelf SetProperty(string key, object? value)
    {
        AddOutput(new SetPropertyStaticDefinition(key, value));
        return Self;
    }

    /// <inheritdoc />
    public TSelf SetProperty(string key, Func<IExchange, object?> factory)
    {
        AddOutput(new SetPropertyFactoryDefinition(key, factory));
        return Self;
    }

    /// <inheritdoc />
    public TSelf SetProperty(string key, IExpression expression)
    {
        AddOutput(new SetPropertyExpressionDefinition(key, expression));
        return Self;
    }

    /// <inheritdoc />
    public TSelf SetPropertyExpression(string key, string template)
    {
        AddOutput(new SetPropertyStringExpressionDefinition(key, template));
        return Self;
    }

    /// <inheritdoc />
    public TSelf RemoveProperty(string key)
    {
        AddOutput(new RemovePropertyDefinition(key));
        return Self;
    }

    // ── Logging ──────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public TSelf Log(string message, LogLevel level = LogLevel.Information)
    {
        AddOutput(new LogStaticDefinition(message, level));
        return Self;
    }

    /// <inheritdoc />
    public TSelf Log(Func<IExchange, string> messageFactory, LogLevel level = LogLevel.Information)
    {
        AddOutput(new LogDynamicDefinition(messageFactory, level));
        return Self;
    }

    // ── Stop / Throw ──────────────────────────────────────────────────────────

    /// <inheritdoc />
    public TSelf Stop()
    {
        AddOutput(new StopDefinition());
        return Self;
    }

    /// <inheritdoc />
    public TSelf ThrowException()
    {
        AddOutput(new RethrowExceptionDefinition());
        return Self;
    }

    /// <inheritdoc />
    public TSelf ThrowException(string message)
    {
        AddOutput(new ThrowMessageDefinition(message));
        return Self;
    }

    /// <inheritdoc />
    public TSelf ThrowException(Exception exception)
    {
        AddOutput(new ThrowExceptionDefinition(exception));
        return Self;
    }

    /// <inheritdoc />
    public TSelf ThrowException(Type exceptionType, string message)
    {
        AddOutput(new ThrowExceptionTypeDefinition(exceptionType, message));
        return Self;
    }

    /// <inheritdoc />
    public TSelf ThrowException<TException>(string? message = null) where TException : Exception, new()
    {
        AddOutput(new ThrowExceptionTypeDefinition(typeof(TException), message));
        return Self;
    }

    // ── Delay / Sampling ─────────────────────────────────────────────────────

    /// <inheritdoc />
    public TSelf Delay(TimeSpan duration)
    {
        AddOutput(new DelayDefinition(duration));
        return Self;
    }

    /// <inheritdoc />
    public TSelf Delay(Func<IExchange, TimeSpan> factory)
    {
        AddOutput(new DelayFactoryDefinition(factory));
        return Self;
    }

    /// <inheritdoc />
    public TSelf Sample(long messageFrequency)
    {
        AddOutput(new SampleCountDefinition(messageFrequency));
        return Self;
    }

    /// <inheritdoc />
    public TSelf Sample(TimeSpan period)
    {
        AddOutput(new SamplePeriodDefinition(period));
        return Self;
    }

    // ── Stream caching ───────────────────────────────────────────────────────

    /// <inheritdoc />
    public TSelf StreamCaching(long? spoolThreshold = null)
    {
        AddOutput(new StreamCachingDefinition(spoolThreshold));
        return Self;
    }

    // ── Validation ───────────────────────────────────────────────────────────

    /// <inheritdoc />
    public TSelf Validate(IMessageValidator validator, bool throwOnFailure = true)
    {
        AddOutput(new ValidateInstanceDefinition(validator, throwOnFailure));
        return Self;
    }

    /// <inheritdoc />
    public TSelf Validate(Func<IExchange, bool> predicate, string errorMessage = "Validation failed", bool throwOnFailure = true)
    {
        AddOutput(new ValidatePredicateDefinition(predicate, errorMessage, throwOnFailure));
        return Self;
    }

    /// <inheritdoc />
    public TSelf ValidateJsonSchema(string schemaJson, bool throwOnFailure = true)
    {
        AddOutput(new ValidateJsonSchemaStringDefinition(schemaJson, throwOnFailure));
        return Self;
    }

    /// <inheritdoc />
    public TSelf ValidateJsonSchema(Json.Schema.JsonSchema schema, bool throwOnFailure = true)
    {
        AddOutput(new ValidateJsonSchemaObjectDefinition(schema, throwOnFailure));
        return Self;
    }

    /// <inheritdoc />
    public TSelf ValidateXsd(string xsdContent, bool throwOnFailure = true)
    {
        AddOutput(new ValidateXsdStringDefinition(xsdContent, throwOnFailure));
        return Self;
    }

    /// <inheritdoc />
    public TSelf ValidateXsd(string? targetNamespace, string xsdContent, bool throwOnFailure = true)
    {
        AddOutput(new ValidateXsdNamespaceDefinition(targetNamespace, xsdContent, throwOnFailure));
        return Self;
    }

    /// <inheritdoc />
    public TSelf ValidateXsd(System.Xml.Schema.XmlSchemaSet schemaSet, bool throwOnFailure = true)
    {
        AddOutput(new ValidateXsdSchemaSetDefinition(schemaSet, throwOnFailure));
        return Self;
    }

    // ── XSLT ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Apache Camel <c>xslt:</c> parity: transform the exchange body through an XSLT stylesheet loaded
    /// from a <b>file</b> (compiled once, reused; <c>xsl:import</c>/<c>xsl:include</c> resolve relative
    /// to the file). BCL <c>XslCompiledTransform</c> engine (XSLT 1.0).
    /// </summary>
    public TSelf Xslt(string stylesheetPath, XsltOutput output = XsltOutput.String, bool failOnNullBody = true, bool allowTemplateFromHeader = false)
    {
        AddOutput(new XsltFileDefinition(stylesheetPath, output, failOnNullBody, allowTemplateFromHeader));
        return Self;
    }

    /// <summary>
    /// Transform the exchange body through an <b>inline</b> XSLT stylesheet document (self-contained;
    /// no <c>xsl:import</c>/<c>xsl:include</c>).
    /// </summary>
    public TSelf XsltContent(string stylesheetXml, XsltOutput output = XsltOutput.String, bool failOnNullBody = true, bool allowTemplateFromHeader = false)
    {
        AddOutput(new XsltContentDefinition(stylesheetXml, output, failOnNullBody, allowTemplateFromHeader));
        return Self;
    }

    // ── Serialization ────────────────────────────────────────────────────────

    /// <inheritdoc />
    public TSelf Marshal(Type serializerType)
    {
        AddOutput(new MarshalDefinition(serializerType));
        return Self;
    }

    /// <inheritdoc />
    public TSelf Marshal<TSerializer>() where TSerializer : class
    {
        AddOutput(new MarshalDefinition(typeof(TSerializer)));
        return Self;
    }

    /// <inheritdoc />
    public TSelf Unmarshal(Type serializerType, Type targetType)
    {
        AddOutput(new UnmarshalDefinition(serializerType, targetType));
        return Self;
    }

    /// <inheritdoc />
    public TSelf Unmarshal<TSerializer, TTarget>() where TSerializer : class
    {
        AddOutput(new UnmarshalDefinition(typeof(TSerializer), typeof(TTarget)));
        return Self;
    }

    /// <summary>
    /// Apache Camel parity: unmarshal the body to <typeparamref name="T"/> using the
    /// <see cref="IDataFormatRegistry"/> entry resolved from the incoming message's ContentType.
    /// </summary>
    public TSelf Unmarshal<T>()
    {
        AddOutput(new ConvertBodyDefinition(typeof(T)));
        return Self;
    }

    /// <summary>Apache Camel parity: convert the exchange body to <typeparamref name="T"/>.</summary>
    public TSelf ConvertBody<T>()
    {
        AddOutput(new ConvertBodyDefinition(typeof(T)));
        return Self;
    }

    /// <summary>Apache Camel parity: convert the exchange body to <paramref name="targetType"/>.</summary>
    public TSelf ConvertBody(Type targetType)
    {
        AddOutput(new ConvertBodyDefinition(targetType));
        return Self;
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
    public TSelf BeginTransaction()
    {
        AddOutput(new BeginTransactionDefinition());
        return Self;
    }

    /// <inheritdoc />
    public TSelf BeginTransaction(Transactions.TransactionPolicy policy)
    {
        AddOutput(new BeginTransactionDefinition(policy));
        return Self;
    }

    /// <inheritdoc />
    public TSelf CommitTransaction()
    {
        AddOutput(new CommitTransactionDefinition());
        return Self;
    }

    /// <inheritdoc />
    public TSelf RollbackTransaction()
    {
        AddOutput(new RollbackTransactionDefinition());
        return Self;
    }

    /// <inheritdoc />
    public TransactionDefinition Transaction(TransactionPolicy? policy = null)
    {
        var def = new TransactionDefinition(policy);
        AddOutput(def);
        return def;
    }

    // ── Replay checkpoints ──────────────────────────────────────────────────

    /// <summary>
    /// Opens a replay checkpoint (save-point). The steps that follow become the marker's <b>body</b>
    /// (the tail re-run on replay). On each pass the exchange is snapshotted into
    /// <c>route.checkpoint</c>. At the very start of a route no <see cref="ReplayableDefinition.End"/>
    /// is needed (the body is the whole pipeline); in the middle or with several markers, close with
    /// <see cref="ReplayableDefinition.EndReplayable"/> or use the lambda-body overload.
    /// </summary>
    /// <param name="name">Save-point name, unique within the route.</param>
    /// <param name="exposed">
    /// When <c>true</c>, the marker is also an addressable public entry (<c>.To(...)</c> / tests);
    /// default <c>false</c> = internal replay only via <see cref="IRouteContext.ReplayAsync"/>.
    /// </param>
    public ReplayableDefinition Replayable(string name, bool exposed = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var def = new ReplayableDefinition(name, exposed);
        AddOutput(def);
        return def;   // opener returns the child — subsequent steps fall into its body
    }

    /// <summary>
    /// Lambda-body form of <see cref="Replayable(string, bool)"/> — unambiguous at any nesting level.
    /// The <paramref name="body"/> configures the marker's tail; the chain stays on the current
    /// definition (no explicit <c>End</c>).
    /// </summary>
    public TSelf Replayable(string name, Action<ReplayableDefinition> body, bool exposed = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(body);
        var def = new ReplayableDefinition(name, exposed);
        AddOutput(def);
        body(def);
        return Self;
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
    public TSelf Traced(string operationName, Action<IExchange> action)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationName);
        AddOutput(new ProcessInstanceDefinition(
            new Telemetry.InstrumentedProcessor(new Processors.DelegateProcessor(action), operationName)));
        return Self;
    }

    /// <inheritdoc />
    public TSelf Traced(string operationName, Func<IExchange, CancellationToken, Task> action)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationName);
        ArgumentNullException.ThrowIfNull(action);
        AddOutput(new ProcessInstanceDefinition(
            new Telemetry.InstrumentedProcessor(new Processors.DelegateProcessor(action), operationName)));
        return Self;
    }

    /// <inheritdoc />
    public TSelf Traced(string operationName, IProcessor processor)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationName);
        ArgumentNullException.ThrowIfNull(processor);
        AddOutput(new ProcessInstanceDefinition(
            new Telemetry.InstrumentedProcessor(processor, operationName)));
        return Self;
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
    public TSelf Metered(string stepName, Action<IExchange> action)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stepName);
        ArgumentNullException.ThrowIfNull(action);
        if (stepName.Contains("${"))
            throw new ArgumentException(
                $"Metered() step name must be a constant to avoid metric cardinality explosion (got dynamic template '{stepName}').",
                nameof(stepName));
        AddOutput(new ProcessInstanceDefinition(
            new Telemetry.MeteredStepProcessor(new Processors.DelegateProcessor(action), stepName)));
        return Self;
    }

    /// <inheritdoc />
    public TSelf Metered(string stepName, Func<IExchange, CancellationToken, Task> action)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stepName);
        ArgumentNullException.ThrowIfNull(action);
        AddOutput(new ProcessInstanceDefinition(
            new Telemetry.MeteredStepProcessor(new Processors.DelegateProcessor(action), stepName)));
        return Self;
    }

    /// <inheritdoc />
    public TSelf Metered(string stepName, IProcessor processor)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stepName);
        ArgumentNullException.ThrowIfNull(processor);
        AddOutput(new ProcessInstanceDefinition(
            new Telemetry.MeteredStepProcessor(processor, stepName)));
        return Self;
    }

    // ── Enrichment ───────────────────────────────────────────────────────────

    /// <inheritdoc />
    public TSelf WireTap(string uri)
    {
        AddOutput(new WireTapDefinition(uri));
        return Self;
    }

    /// <inheritdoc />
    public TSelf WireTap(string uri, Action<IExchange> onPrepare)
    {
        AddOutput(new WireTapDefinition(uri, onPrepare: onPrepare));
        return Self;
    }

    /// <inheritdoc />
    public TSelf WireTap(string uri, Func<IExchange, object?> newBodyFactory)
    {
        AddOutput(new WireTapDefinition(uri, newBodyFactory: newBodyFactory));
        return Self;
    }

    /// <inheritdoc />
    public TSelf WireTap(string uri, Action<IExchange> onPrepare, Func<IExchange, object?> newBodyFactory)
    {
        AddOutput(new WireTapDefinition(uri, onPrepare, newBodyFactory));
        return Self;
    }

    /// <inheritdoc />
    public TSelf WireTap(Func<IExchange, string> uriFactory)
    {
        AddOutput(new WireTapDynamicDefinition(uriFactory));
        return Self;
    }

    /// <inheritdoc />
    public TSelf WireTap(Func<IExchange, string> uriFactory, Action<IExchange> onPrepare)
    {
        AddOutput(new WireTapDynamicDefinition(uriFactory, onPrepare: onPrepare));
        return Self;
    }

    /// <inheritdoc />
    public TSelf WireTap(Func<IExchange, string> uriFactory, Func<IExchange, object?> newBodyFactory)
    {
        AddOutput(new WireTapDynamicDefinition(uriFactory, newBodyFactory: newBodyFactory));
        return Self;
    }

    /// <inheritdoc />
    public TSelf WireTap(Func<IExchange, string> uriFactory, Action<IExchange> onPrepare, Func<IExchange, object?> newBodyFactory)
    {
        AddOutput(new WireTapDynamicDefinition(uriFactory, onPrepare, newBodyFactory));
        return Self;
    }

    /// <inheritdoc />
    public TSelf Enrich(string resourceUri, Func<IExchange, IExchange, IExchange> mergeStrategy)
    {
        AddOutput(new EnrichDefinition(resourceUri, mergeStrategy));
        return Self;
    }

    /// <inheritdoc />
    public TSelf Enrich(Func<IExchange, string> uriFactory, Func<IExchange, IExchange, IExchange> mergeStrategy)
    {
        AddOutput(new EnrichDynamicDefinition(uriFactory, mergeStrategy));
        return Self;
    }

    /// <summary>Apache Camel parity: poll an external endpoint and merge the polled result.</summary>
    public TSelf PollEnrich(
        string resourceUri,
        Func<IExchange, IExchange?, IExchange> mergeStrategy,
        TimeSpan? timeout = null)
    {
        AddOutput(new PollEnrichDefinition(resourceUri, mergeStrategy, timeout));
        return Self;
    }

    /// <inheritdoc />
    public TSelf PollEnrich(
        Func<IExchange, string> uriFactory,
        Func<IExchange, IExchange?, IExchange> mergeStrategy,
        TimeSpan? timeout = null)
    {
        AddOutput(new PollEnrichDynamicDefinition(uriFactory, mergeStrategy, timeout));
        return Self;
    }

    /// <summary>Apache Camel parity: route to a list of recipient URIs computed at runtime.</summary>
    public TSelf RecipientList(
        Func<IExchange, IEnumerable<string>> recipientListFactory,
        bool parallelProcessing = false,
        bool stopOnException = false,
        Func<IExchange, IExchange, IExchange>? aggregationStrategy = null)
    {
        AddOutput(new RecipientListDefinition(
            recipientListFactory, parallelProcessing, stopOnException, aggregationStrategy));
        return Self;
    }

    /// <summary>Apache Camel parity: iteratively route to URIs returned by a routing function.</summary>
    public TSelf DynamicRouter(Func<IExchange, string?> routingFunction)
    {
        AddOutput(new DynamicRouterDefinition(routingFunction));
        return Self;
    }

    /// <summary>
    /// Apache Camel parity: Routing Slip — pipe the exchange through a list of endpoints computed
    /// <b>once</b> up front by <paramref name="slipFactory"/> (contrast Dynamic Router, which recomputes
    /// per hop). The same exchange is threaded through the endpoints in sequence (Out→In between hops).
    /// </summary>
    public TSelf RoutingSlip(Func<IExchange, IEnumerable<string>> slipFactory, bool ignoreInvalidEndpoints = false)
    {
        AddOutput(new RoutingSlipDefinition(slipFactory, ignoreInvalidEndpoints));
        return Self;
    }

    /// <summary>
    /// Apache Camel parity: Routing Slip from an <see cref="IExpression"/> yielding a delimited URI
    /// string (e.g. <c>.RoutingSlip(Header("mySlip"))</c>). Default delimiter is <c>,</c>.
    /// </summary>
    public TSelf RoutingSlip(IExpression slip, string uriDelimiter = ",", bool ignoreInvalidEndpoints = false)
    {
        AddOutput(new RoutingSlipDefinition(slip, uriDelimiter, ignoreInvalidEndpoints));
        return Self;
    }

    /// <summary>
    /// Apache Camel parity: Routing Slip from a <c>${...}</c> template (or a literal like
    /// <c>"direct:a,direct:b"</c>) resolving to a delimited URI string. Default delimiter is <c>,</c>.
    /// </summary>
    public TSelf RoutingSlip(string slipTemplate, string uriDelimiter = ",", bool ignoreInvalidEndpoints = false)
    {
        AddOutput(new RoutingSlipDefinition(slipTemplate, uriDelimiter, ignoreInvalidEndpoints));
        return Self;
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
    public ThreadsDefinition Threads(int poolSize)
    {
        var def = new ThreadsDefinition(poolSize);
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
    public TSelf Bean<TService>(Func<TService, IExchange, CancellationToken, Task> method)
    {
        ArgumentNullException.ThrowIfNull(method);
        AddOutput(new BeanDefinition(
            typeof(TService),
            (svc, exchange, ct) => method((TService)svc, exchange, ct)));
        return Self;
    }

    /// <inheritdoc />
    public TSelf Bean<TService>(Func<TService, IExchange, Task> method)
    {
        ArgumentNullException.ThrowIfNull(method);
        AddOutput(new BeanDefinition(
            typeof(TService),
            (svc, exchange, _) => method((TService)svc, exchange)));
        return Self;
    }

    /// <inheritdoc />
    public TSelf Bean<TService>(Action<TService, IExchange> method)
    {
        ArgumentNullException.ThrowIfNull(method);
        AddOutput(new BeanDefinition(
            typeof(TService),
            (svc, exchange, _) => { method((TService)svc, exchange); return Task.CompletedTask; }));
        return Self;
    }

    // ── Scatter-Gather ────────────────────────────────────────────────────────

    /// <inheritdoc />
    public TSelf ScatterGather(
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
        return Self;
    }

    /// <inheritdoc />
    public TSelf ScatterGather(Action<IScatterGatherDefinition> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var def = new ScatterGatherDefinition();
        configure(def);
        if (def.AggregationStrategy is null)
            throw new InvalidOperationException("AggregationStrategy is required for ScatterGather.");
        if (def.StaticRecipients is null && def.DynamicRecipients is null)
            throw new InvalidOperationException("Recipients are required for ScatterGather.");
        AddOutput(def);
        return Self;
    }

    // ── Saga ──────────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public TSelf Saga(Action<ISagaDefinition> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var def = new SagaDefinition();
        configure(def);
        if (def.Entries.Count == 0)
            throw new InvalidOperationException("Saga must have at least one step.");
        AddOutput(def);
        return Self;
    }

    /// <inheritdoc />
    public SagaDefinition Saga()
    {
        var def = new SagaDefinition();
        AddOutput(def);
        return def;
    }

    // ── LoadBalance ───────────────────────────────────────────────────────────

    /// <inheritdoc />
    public TSelf LoadBalance(ILoadBalancerStrategy strategy, params string[] uris)
    {
        ArgumentNullException.ThrowIfNull(strategy);
        if (uris is null || uris.Length == 0)
            throw new ArgumentException("At least one endpoint URI is required.", nameof(uris));

        var def = new LoadBalancerDefinition();
        ((ILoadBalancerDefinition)def).Endpoints(uris);
        ((ILoadBalancerDefinition)def).Strategy(strategy);
        AddOutput(def);
        return Self;
    }

    /// <inheritdoc />
    public TSelf LoadBalance(Action<ILoadBalancerDefinition> configure)
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
        return Self;
    }

    // ── Normalizer ────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public TSelf Normalize(Action<INormalizerDefinition> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var def = new NormalizerDefinition();
        configure(def);
        if (def.IsEmpty)
            throw new InvalidOperationException("Normalizer requires at least one When clause.");
        AddOutput(def);
        return Self;
    }

    // ── Exception handling ────────────────────────────────────────────────────

    /// <inheritdoc />
    public TSelf ExceptionHandled()
    {
        AddOutput(new ExceptionHandledDefinition());
        return Self;
    }

    /// <inheritdoc />
    public TSelf RollbackAll()
    {
        AddOutput(new RollbackAllDefinition());
        return Self;
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
    public TSelf Cluster(bool value = true)
    {
        _cluster = value;
        return Self;
    }

    /// <inheritdoc />
    public bool GetCluster() => _cluster;

    /// <inheritdoc />
    public TSelf MessageHistory(bool value = true)
    {
        _messageHistory = value;
        return Self;
    }

    /// <inheritdoc />
    public bool? GetMessageHistory() => _messageHistory;

    /// <inheritdoc />
    public TSelf RoutePolicy(IRoutePolicy policy)
    {
        _routePolicy = policy ?? throw new ArgumentNullException(nameof(policy));
        return Self;
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

    private protected static bool ConvertToBoolean(object? value) => value switch
    {
        bool b => b,
        string s => bool.TryParse(s, out var result) ? result : !string.IsNullOrEmpty(s),
        null => false,
        _ => true
    };
}

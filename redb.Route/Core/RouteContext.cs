using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.Components;
using redb.Route.Configuration;
using redb.Route.Definitions;
using redb.Route.Processors;
using redb.Route.Serialization;
using redb.Route.Telemetry;

namespace redb.Route.Core;

/// <summary>
/// Central runtime context: component registry, endpoint cache, route compilation and lifecycle,
/// properties, service locator, and three-tier exception handling.
/// Replaces the former RouteEngine — like Apache Camel's CamelContext, this is the single
/// entry point that owns routes from definition through execution.
/// </summary>
public class RouteContext : IRouteContext, IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, IComponent> _components = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, IEndpoint> _endpoints = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<IConsumer> _consumers = [];
    private readonly List<IProducer> _producers = [];
    // Producers are tracked from route compilation, from processors on first use and from producer templates, which
    // may run on several threads at once.
    private readonly object _producersLock = new();

    // Context-level properties
    private readonly ConcurrentDictionary<string, object?> _properties = new(StringComparer.OrdinalIgnoreCase);

    // Named object registry
    private readonly ConcurrentDictionary<string, object> _registry = new(StringComparer.OrdinalIgnoreCase);

    // Type-based service locator
    private readonly ConcurrentDictionary<Type, object> _services = new();

    // DI container
    private IServiceProvider? _serviceProvider;

    // Global exception handlers: ExceptionType → Processor
    private readonly ConcurrentDictionary<Type, IProcessor> _globalExceptionHandlers = new();

    // Local exception handlers: RouteId → (ExceptionType → Processor)
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<Type, IProcessor>> _localExceptionHandlers = new();

    // The two tables above hold what the user registered and outlive a restart. The error scope of each route — its
    // own OnException blocks, its builder's, the context layer — is rebuilt on every compile (RouteErrorScope).
    private readonly ConcurrentDictionary<string, RouteErrorScope> _errorScopes = new();

    // The error scope of the route being compiled, for a detached body (.Threads()) to be wrapped by. Null while a
    // handler body or a detached block (OnCompletion) compiles: those run outside the route's error handling.
    private RouteErrorScope? _compilingErrorScope;

    // Route compilation
    // Not readonly: AddService(ILoggerFactory, ...) adopts a factory registered after construction
    // and back-fills component loggers, so a context created without one still logs consumer failures.
    private ILoggerFactory? _loggerFactory;
    private ILogger? _logger;
    private readonly RouteEngineOptions _options;

    /// <summary>The engine's stream caching options (<see cref="RouteEngineOptions.StreamCaching"/>).</summary>
    internal Configuration.StreamCacheOptions StreamCacheOptions => _options.StreamCaching;

    /// <summary>The engine's attachment options (<see cref="RouteEngineOptions.Attachments"/>).</summary>
    internal Configuration.AttachmentOptions AttachmentOptions => _options.Attachments;

    // The static targets of the route being compiled (To, WireTap, Enrich, load-balancer targets, fixed recipients):
    // their endpoints are created once the route compiles, as Camel resolves a static to(...) at route startup.
    private List<string>? _compilingStaticEndpoints;
    // Resolved static targets an intercept with skipSendToOriginalEndpoint matches: the original may never be sent to
    // (a dry run whose real component is absent), so their endpoints are not created up front.
    private HashSet<string>? _compilingSkippedStaticEndpoints;

    /// <summary>
    /// A processor with a fixed target notes it while its route compiles; the endpoint is then created before the route
    /// starts, so an unknown scheme, an unknown URI parameter or a missing <c>{{key}}</c> fails the start. Outside a
    /// compilation (a processor made while messages flow) nothing is noted.
    /// </summary>
    internal void NoteStaticEndpoint(string uri) => _compilingStaticEndpoints?.Add(uri);

    /// <summary>The engine's <see cref="RouteEngineOptions.EnableTelemetry"/>, read by transport spans too.</summary>
    internal bool TelemetryEnabled => _options.EnableTelemetry;
    private readonly List<RouteBuilder> _builders = [];
    // Builder types added by AddRoutes(Assembly[]); an explicit AddRoutes(builder) of one of them is a duplicate.
    private readonly HashSet<Type> _discoveredBuilderTypes = [];

    /// <summary>Registered builders (test-kit seam for AdviceWith; definitions are built on demand).</summary>
    internal IReadOnlyList<RouteBuilder> RouteBuilders => _builders;

    /// <summary>
    /// Set by AdviceWith after it has run every builder's <c>Configure()</c> ahead of <see cref="Start"/>
    /// and rewritten the definitions; <see cref="CompileRoutesFromBuilders"/> then compiles them as-is
    /// instead of rebuilding (which would discard the advice).
    /// </summary>
    internal bool DefinitionsPrebuilt { get; set; }
    private readonly List<CompiledRoute> _routes = [];
    private readonly IInflightRepository _inflightRepository = new DefaultInflightRepository();

    // Copy-on-write: the array is replaced on registration and read as a snapshot per notification, so a
    // listener registered while exchanges flow (NotifyBuilder on a running context, a listener adding
    // another listener from a callback) never races an enumeration.
    private IRouteLifecycleListener[] _lifecycleListeners = [];
    private readonly object _lifecycleListenersLock = new();
    private readonly List<IRoutePolicyFactory> _policyFactories = [];

    private readonly SemaphoreSlim _routeLock = new(1, 1);

    // Replay checkpoints: compiled marker body per (routeId, markerName). Populated at compile time
    // so ReplayAsync can re-run the tail directly (no synthetic endpoint) and Tsak can list markers
    // before any traffic. See docs/REPLAY_CHECKPOINTS_PLAN.md.
    private readonly Dictionary<(string RouteId, string Marker), (IProcessor Body, bool Exposed)> _checkpoints
        = new();
    // Route currently being compiled — set around definition.CreateProcessor so a nested
    // ReplayableDefinition can register itself against the right routeId.
    private string? _compilingRouteId;
    // Message-history state for the route currently being compiled (set alongside _compilingRouteId).
    private bool _compilingMessageHistory;
    private int _compilingNodeCounter;
    private readonly List<IAsyncDisposable> _disposableProcessors = new();

    private volatile bool _started;

    /// <summary>
    /// Optional callback invoked after routes are compiled but before consumers are started.
    /// Allows external code (e.g., Tsak StateStore) to override <see cref="CompiledRoute.AutoStart"/>
    /// for individual routes before the context decides which consumers to skip.
    /// </summary>
    public Func<IReadOnlyList<CompiledRoute>, Task>? OnBeforeRoutesStart { get; set; }

    /// <summary>
    /// Creates a new route context with built-in components (Direct, Timer, Seda, Mock, Log)
    /// and registers the provided <see cref="IServiceProvider"/> for DI resolution.
    /// </summary>
    /// <param name="serviceProvider">DI service provider.</param>
    /// <param name="contextId">Optional context identifier. If null, a new GUID is generated.</param>
    /// <param name="loggerFactory">Optional logger factory for route compilation diagnostics.</param>
    /// <param name="options">Engine options (telemetry, metrics, shutdown, etc.).</param>
    public RouteContext(IServiceProvider serviceProvider, string? contextId = null,
        ILoggerFactory? loggerFactory = null, RouteEngineOptions? options = null)
        : this(contextId, loggerFactory, options)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        SetServiceProvider(serviceProvider);
    }

    /// <summary>
    /// Creates a new route context with built-in components (Direct, Timer, Seda, Mock, Log).
    /// </summary>
    /// <param name="contextId">Optional context identifier. If null, a new GUID is generated.</param>
    /// <param name="loggerFactory">Optional logger factory for route compilation diagnostics.</param>
    /// <param name="options">Engine options (telemetry, metrics, shutdown, etc.).</param>
    public RouteContext(string? contextId = null, ILoggerFactory? loggerFactory = null, RouteEngineOptions? options = null)
    {
        ContextId = contextId ?? Guid.NewGuid().ToString("N");
        IsContextIdGenerated = contextId is null;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory?.CreateLogger<RouteContext>();
        _options = options ?? new RouteEngineOptions();

        // A collectible load context of a host's module unloads only once the .NET library caches let its types go.
        CollectibleContextCleanup.EnsureWatching(_logger);

        if (_logger is not null)
            _services[typeof(ILogger)] = _logger;

        // Expose the logger factory via the service locator so definitions
        // (the Log* definitions and RichLogScopeDefinition, etc.) can resolve it
        // through IRouteContext.GetService<ILoggerFactory>() without reaching for DI.
        if (loggerFactory is not null)
            _services[typeof(ILoggerFactory)] = loggerFactory;

        // Register built-in components
        _components["direct"] = SetComponentContext(new DirectComponent());
        _components["timer"] = SetComponentContext(new TimerComponent());
        _components["seda"] = SetComponentContext(new SedaComponent());
        _components["mock"] = SetComponentContext(new MockComponent());
        _components["log"] = SetComponentContext(new LogComponent(loggerFactory));
        _components["direct-vm"] = SetComponentContext(new DirectVmComponent());
        _components["vm"] = SetComponentContext(new VmComponent());
        _components["xslt"] = SetComponentContext(new Xslt.XsltComponent());
        _components["controlbus"] = SetComponentContext(new ControlBus.ControlBusComponent());
        _components["bean"] = SetComponentContext(new Components.Bean.BeanComponent());

        // Register built-in services
        _services[typeof(IDataFormatRegistry)] = new DataFormatRegistry();
    }

    private IComponent SetComponentContext(IComponent component)
    {
        if (component is ComponentBase baseComponent)
        {
            baseComponent.Context = this;
            baseComponent.Logger ??= _loggerFactory?.CreateLogger(component.GetType());
        }
        return component;
    }

    // ── Identity ──

    /// <inheritdoc />
    public string ContextId { get; }

    /// <summary>
    /// Whether <see cref="ContextId"/> was generated (a new GUID) rather than given: then it differs on every start and
    /// on every node, so nothing persisted under it — a job store's scheduler name — can be found again.
    /// </summary>
    public bool IsContextIdGenerated { get; }

    /// <inheritdoc />
    public bool IsStarted => _started;

    /// <summary>Repository tracking all in-flight exchanges across routes in this context.</summary>
    public IInflightRepository InflightRepository => _inflightRepository;

    // ── Properties ──

    /// <inheritdoc />
    public object? this[string key]
    {
        get => _properties.TryGetValue(key, out var value) ? value : null;
        set => _properties[key] = value;
    }

    /// <inheritdoc />
    public T? GetProperty<T>(string key)
    {
        if (!_properties.TryGetValue(key, out var value) || value is null)
            return default;
        if (value is T typed)
            return typed;
        try
        {
            return (T)Convert.ChangeType(value, typeof(T));
        }
        catch
        {
            return default;
        }
    }

    /// <inheritdoc />
    public IRouteContext SetProperty<T>(string key, T value)
    {
        _properties[key] = value;
        return this;
    }

    // ── Registry ──

    /// <inheritdoc />
    public IRouteContext AddToRegistry(string key, object value)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(value);
        _registry[key] = value;
        return this;
    }

    // ── Replay checkpoints ──

    /// <summary>
    /// Registers a compiled <c>.Replayable</c> marker body for the route currently being compiled.
    /// Called by <see cref="Definitions.ReplayableDefinition.CreateProcessor"/>; keyed on the
    /// route being compiled (<see cref="_compilingRouteId"/>) plus the marker name.
    /// </summary>
    internal void RegisterCheckpoint(string markerName, IProcessor body, bool exposed)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(markerName);
        ArgumentNullException.ThrowIfNull(body);
        var routeId = _compilingRouteId
            ?? throw new InvalidOperationException(
                "RegisterCheckpoint called outside route compilation — no route id in scope.");
        if (!_checkpoints.TryAdd((routeId, markerName), (body, exposed)))
            throw new InvalidOperationException(
                $"Duplicate replay marker '{markerName}' on route '{routeId}'. Marker names must be unique within a route.");

        // exposed:true → make the marker addressable as a direct: endpoint so other routes can
        // .To(...) it (and ProducerTemplate can Send to it). Internal (default) markers register no
        // endpoint — they are reachable only via ReplayAsync. Wired at compile time so the body is
        // ready before any producer sends at runtime.
        if (exposed)
        {
            var uri = RouteCheckpoint.EndpointUri(routeId, markerName);
            if (GetEndpoint(uri) is Components.DirectEndpoint endpoint)
                endpoint.RegisterConsumerProcessor(body);
            else
                throw new InvalidOperationException(
                    $"Could not register exposed replay endpoint '{uri}' — direct component missing?");
        }
    }

    /// <inheritdoc />
    public async Task ReplayAsync(string routeId, string markerName, IExchange snapshot, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(routeId);
        ArgumentException.ThrowIfNullOrWhiteSpace(markerName);
        ArgumentNullException.ThrowIfNull(snapshot);

        if (!_checkpoints.TryGetValue((routeId, markerName), out var entry))
            throw new InvalidOperationException(
                $"No replay checkpoint '{markerName}' on route '{routeId}'. " +
                $"Ensure the route declares .Replayable(\"{markerName}\") and has been compiled.");

        // The tail creates its OWN scoped SQL/redb services lazily from the context provider (fresh
        // connections — old ones from the failed run are dead/rolled-back, so re-creating is correct):
        // nothing to pre-mint or "capture" here. But because we invoke the body directly, bypassing
        // the normal consumer→dispose lifecycle, WE must dispose whatever scopes the tail cached on
        // the snapshot — else every replay leaks a connection. Exchange.ReleaseScopes does exactly
        // that, and it re-arms itself after every sweep, so a snapshot released before this replay
        // needs no special preparation.
        try
        {
            // Run the marker's tail directly from the frozen snapshot — same compiled body as inline
            // execution (processors are re-entrant). No synthetic endpoint.
            await entry.Body.Process(snapshot, ct).ConfigureAwait(false);
        }
        finally
        {
            if (snapshot is Exchange ex)
                await ex.ReleaseScopes().ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public IReadOnlyCollection<(string RouteId, string MarkerName)> GetReplayMarkers()
        => _checkpoints.Keys.Select(k => (k.RouteId, k.Marker)).ToArray();

    /// <inheritdoc />
    public T? GetFromRegistry<T>(string key)
    {
        if (key.StartsWith('#'))
            key = key[1..];
        if (_registry.TryGetValue(key, out var value) && value is T typed)
            return typed;
        return default;
    }

    // ── Service Locator ──

    /// <inheritdoc />
    public void AddService(Type serviceType, object service)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        ArgumentNullException.ThrowIfNull(service);
        _services[serviceType] = service;

        // A logger factory registered after construction must actually reach components: adopt it as
        // the context's factory (used by SetComponentContext for components added later and by the
        // route compiler for OnException/statistics loggers) and back-fill loggers onto components that
        // were registered before it. Without this, a context created with no factory logs nothing —
        // consumer failures pass silently.
        if (service is ILoggerFactory factory)
            AdoptLoggerFactory(factory);
    }

    /// <summary>
    /// Adopts <paramref name="factory"/> as the context logger factory and back-fills a logger onto
    /// every already-registered component that does not have one.
    /// </summary>
    private void AdoptLoggerFactory(ILoggerFactory factory)
    {
        _loggerFactory = factory;
        _logger ??= factory.CreateLogger<RouteContext>();
        if (!_services.ContainsKey(typeof(ILogger)) && _logger is not null)
            _services[typeof(ILogger)] = _logger;

        foreach (var component in _components.Values)
        {
            if (component is ComponentBase baseComponent)
                baseComponent.Logger ??= factory.CreateLogger(component.GetType());
        }
    }

    /// <inheritdoc />
    public T? GetService<T>() where T : class
    {
        return _services.TryGetValue(typeof(T), out var service) && service is T typed
            ? typed
            : default;
    }

    // ── DI ──

    /// <inheritdoc />
    public IRouteContext SetServiceProvider(IServiceProvider serviceProvider)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        _serviceProvider = serviceProvider;
        return this;
    }

    /// <inheritdoc />
    public IServiceProvider? GetServiceProvider() => _serviceProvider;

    // ── Component Management ──

    /// <inheritdoc />
    public void AddComponent(IComponent component)
    {
        ArgumentNullException.ThrowIfNull(component);
        var configured = SetComponentContext(component);
        _components[component.Scheme] = configured;
        foreach (var alias in component.AlternateSchemes)
            _components[alias] = configured;
    }

    /// <inheritdoc />
    public T? GetComponent<T>(string? scheme = null) where T : class, IComponent
    {
        if (!string.IsNullOrEmpty(scheme))
        {
            return _components.TryGetValue(scheme, out var c) && c is T typed ? typed : default;
        }

        foreach (var component in _components.Values)
        {
            if (component is T match)
                return match;
        }

        return default;
    }

    /// <inheritdoc />
    public bool HasComponent(string scheme) => _components.ContainsKey(scheme);

    /// <inheritdoc />
    public IReadOnlyList<string> GetComponentNames() => _components.Keys.ToList().AsReadOnly();

    /// <inheritdoc />
    public bool RemoveComponent(string scheme)
    {
        ArgumentException.ThrowIfNullOrEmpty(scheme);
        return _components.TryRemove(scheme, out _);
    }

    // ── Endpoint Management ──

    /// <inheritdoc />
    public IEndpoint GetEndpoint(string uri)
    {
        var resolved = ResolvePlaceholders(uri);
        var parsed = EndpointUriParser.Parse(resolved);
        return _endpoints.GetOrAdd(parsed.NormalizedKey, _ => CreateEndpoint(parsed));
    }

    /// <summary>
    /// Expands Camel-style <c>{{key}}</c> / <c>{{key:default}}</c> property placeholders in an endpoint URI
    /// before it is parsed, so every connector's URIs externalise their configuration for free. Values come
    /// from <c>IConfiguration</c> (environment, appsettings, user-secrets) first, then the context's own
    /// properties as a container-free fallback. No-op for the common case of a URI without placeholders.
    /// </summary>
    // Internal (not private): the XML loader resolves {{...}} in <route enabled=…> through the
    // context's own lookup chain instead of keeping a second copy of it.
    internal string ResolvePlaceholders(string uri)
    {
        if (!PropertyPlaceholderResolver.HasPlaceholder(uri))
            return uri;
        return PropertyPlaceholderResolver.Resolve(uri, LookupPlaceholderValue);
    }

    private string? LookupPlaceholderValue(string key)
    {
        // IConfiguration already layers env vars + appsettings + user-secrets with its own precedence,
        // so a single indexer lookup covers what Camel needs env:/sys: prefix functions for.
        var config = GetService<IConfiguration>() ?? _serviceProvider?.GetService<IConfiguration>();
        var fromConfig = config?[key];
        if (!string.IsNullOrEmpty(fromConfig))
            return fromConfig;

        // Container-free fallback: values set via context.SetProperty(...).
        return GetProperty<string>(key);
    }

    // ── Exception Handling ──

    /// <inheritdoc />
    public IErrorHandler? ErrorHandler { get; set; }

    /// <inheritdoc />
    public bool HasExceptionRoute<TException>() where TException : Exception
    {
        var exType = typeof(TException);

        // Check global handlers
        if (FindHandler(_globalExceptionHandlers, exType) is not null)
            return true;

        // Check all local handler sets
        foreach (var localHandlers in _localExceptionHandlers.Values)
        {
            if (FindHandler(localHandlers, exType) is not null)
                return true;
        }

        // Check the OnException blocks of every route and of its builder
        foreach (var scope in _errorScopes.Values)
        {
            if (scope.Handles(exType))
                return true;
        }

        return false;
    }

    /// <inheritdoc />
    public void AddGlobalExceptionHandler<TException>(IProcessor processor) where TException : Exception
    {
        ArgumentNullException.ThrowIfNull(processor);
        if (!_globalExceptionHandlers.TryAdd(typeof(TException), processor))
            throw new InvalidOperationException(
                $"Global exception handler for {typeof(TException).Name} is already registered.");
    }

    /// <inheritdoc />
    public void AddGlobalExceptionHandler(Type exceptionType, IProcessor processor)
    {
        ArgumentNullException.ThrowIfNull(exceptionType);
        ArgumentNullException.ThrowIfNull(processor);
        if (!_globalExceptionHandlers.TryAdd(exceptionType, processor))
            throw new InvalidOperationException(
                $"Global exception handler for {exceptionType.Name} is already registered.");
    }

    /// <inheritdoc />
    public void AddLocalExceptionHandler(string routeId, Type exceptionType, IProcessor processor)
    {
        ArgumentException.ThrowIfNullOrEmpty(routeId);
        ArgumentNullException.ThrowIfNull(exceptionType);
        ArgumentNullException.ThrowIfNull(processor);

        var handlers = _localExceptionHandlers.GetOrAdd(routeId, _ => new ConcurrentDictionary<Type, IProcessor>());
        handlers[exceptionType] = processor;
    }

    /// <inheritdoc />
    public async Task HandleException(IExchange exchange, Exception exception, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(exchange);
        ArgumentNullException.ThrowIfNull(exception);

        // Tier 4: No handler found — throw
        if (!await HandleWithContextHandlers(exchange, exception, exchange.RouteId, ct).ConfigureAwait(false))
            throw new InvalidOperationException(
                $"No exception handler found for {exception.GetType().Name} in route '{exchange.RouteId}'.",
                exception);
    }

    /// <summary>
    /// The context's error handling, in its order: a local handler of <paramref name="routeId"/> (tier 1), a global
    /// handler (tier 2), <see cref="ErrorHandler"/> (tier 3). Runs the first that matches and returns <c>true</c>;
    /// <c>false</c> when none does. The one lookup behind <see cref="HandleException"/> and the outermost layer of
    /// every route's error scope (<see cref="ContextErrorLayerProcessor"/>).
    /// </summary>
    internal async Task<bool> HandleWithContextHandlers(IExchange exchange, Exception exception, string? routeId,
        CancellationToken ct)
    {
        // Tier 1: Local route handler
        if (!string.IsNullOrEmpty(routeId) &&
            _localExceptionHandlers.TryGetValue(routeId, out var localHandlers) &&
            FindHandler(localHandlers, exception.GetType()) is { } local)
        {
            Caught(exchange, exception);
            await local.Process(exchange, ct).ConfigureAwait(false);
            return true;
        }

        // Tier 2: Global handler
        if (FindHandler(_globalExceptionHandlers, exception.GetType()) is { } global)
        {
            Caught(exchange, exception);
            await global.Process(exchange, ct).ConfigureAwait(false);
            return true;
        }

        // Tier 3: ErrorHandler fallback
        if (ErrorHandler is { } errorHandler)
        {
            Caught(exchange, exception);
            await errorHandler.HandleError(exchange, exception, ct).ConfigureAwait(false);
            return true;
        }

        return false;

        // A handler takes the failure: it is on the exchange, and kept for predicate/expression usage. A failure no
        // handler took leaves the exchange as it was.
        static void Caught(IExchange exchange, Exception exception)
        {
            exchange.Properties["ExceptionCaught"] = exception;
            exchange.Exception = exception;
        }
    }

    // ── Route Management ──

    /// <summary>Gets the compiled routes (available after Start).</summary>
    public IReadOnlyList<CompiledRoute> Routes => _routes;

    /// <summary>Adds a route builder containing route definitions.</summary>
    /// <param name="builder">Route builder to add.</param>
    /// <returns>This context for fluent chaining.</returns>
    /// <exception cref="InvalidOperationException">The builder's type was already added by assembly discovery.</exception>
    public RouteContext AddRoutes(RouteBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        if (_discoveredBuilderTypes.Contains(builder.GetType()))
            throw new InvalidOperationException(
                $"Route builder '{builder.GetType().FullName}' was already added by assembly discovery. " +
                "Leave it out of the discovery with the filter, or do not add it again.");
        _builders.Add(builder);
        return this;
    }

    /// <summary>
    /// Adds every route builder of <paramref name="assemblies"/>: each public, non-abstract, closed class deriving from
    /// <see cref="RouteBuilder"/>, created through its public parameterless constructor (Apache Camel's routes
    /// discovery). For constructor injection use <c>RedbRouteBuilder.AddRouteBuilders</c> in the host.
    /// </summary>
    /// <param name="assemblies">Assemblies to scan; at least one.</param>
    /// <returns>This context for fluent chaining.</returns>
    /// <exception cref="InvalidOperationException">
    /// A builder cannot be created, its type is already added, or an assembly's types cannot all be loaded. Nothing is
    /// added then.
    /// </exception>
    public RouteContext AddRoutes(params Assembly[] assemblies)
        => AddDiscoveredRoutes(assemblies, filter: null);

    /// <summary>
    /// <see cref="AddRoutes(Assembly[])"/> with a filter choosing which builders to take — the place for a condition
    /// (environment, configuration) and for leaving a builder out.
    /// </summary>
    /// <param name="assemblies">Assemblies to scan; at least one.</param>
    /// <param name="filter">Returns <c>true</c> for a builder type to add.</param>
    /// <returns>This context for fluent chaining.</returns>
    /// <exception cref="InvalidOperationException">As <see cref="AddRoutes(Assembly[])"/>.</exception>
    public RouteContext AddRoutes(IEnumerable<Assembly> assemblies, Func<Type, bool> filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        return AddDiscoveredRoutes(assemblies, filter);
    }

    private RouteContext AddDiscoveredRoutes(IEnumerable<Assembly> assemblies, Func<Type, bool>? filter)
    {
        var types = RouteBuilderDiscovery.FindBuilderTypes(assemblies, filter);

        // A type already here — added explicitly or by an earlier discovery — would compile its routes twice and fail
        // only at Start with a duplicate route id; said now, with the type's name.
        var present = _builders.Select(b => b.GetType()).ToHashSet();
        foreach (var type in types)
        {
            if (present.Contains(type))
                throw new InvalidOperationException(
                    $"Route builder '{type.FullName}' is already added to this context. " +
                    "Leave it out of the discovery with the filter, or do not add it explicitly.");
        }

        // Every builder is created before any is added: a failure leaves the context as it was.
        var builders = types.Select(RouteBuilderDiscovery.Create).ToList();
        foreach (var builder in builders)
        {
            _builders.Add(builder);
            _discoveredBuilderTypes.Add(builder.GetType());
        }

        _logger?.LogInformation("Discovered {Count} route builder(s): {Builders}",
            builders.Count, string.Join(", ", types.Select(t => t.FullName)));
        return this;
    }

    /// <summary>Adds routes using an inline configure action.</summary>
    /// <param name="configure">Action to configure routes.</param>
    /// <returns>This context for fluent chaining.</returns>
    public RouteContext AddRoutes(Action<InlineRouteBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var builder = new InlineRouteBuilder(configure);
        _builders.Add(builder);
        return this;
    }

    /// <summary>
    /// Adds the default route configuration with an inline action — the inline form of a
    /// <see cref="RouteConfigurationBuilder"/>: error handling for every route of the context that has no
    /// configurations assigned.
    /// <example>
    /// <code>
    /// context.AddRouteConfiguration(c => c
    ///     .OnException&lt;Exception&gt;().MaximumRedeliveries(3).Handled().Log("failed: ${exception.message}"));
    /// </code>
    /// </example>
    /// </summary>
    /// <param name="configure">Action that declares the handlers of the configuration.</param>
    /// <returns>This context for fluent chaining.</returns>
    public RouteContext AddRouteConfiguration(Action<RouteConfigurationDefinition> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        _builders.Add(new InlineRouteConfigurationBuilder(null, configure));
        return this;
    }

    /// <summary>
    /// Adds a route configuration with an id, for the routes that name it with <c>.RouteConfigurationId("id")</c>.
    /// </summary>
    /// <param name="id">The id of the configuration, unique within the context.</param>
    /// <param name="configure">Action that declares the handlers of the configuration.</param>
    /// <returns>This context for fluent chaining.</returns>
    public RouteContext AddRouteConfiguration(string id, Action<RouteConfigurationDefinition> configure)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(configure);
        _builders.Add(new InlineRouteConfigurationBuilder(id, configure));
        return this;
    }

    private sealed class InlineRouteConfigurationBuilder(string? id, Action<RouteConfigurationDefinition> configure)
        : RouteConfigurationBuilder
    {
        protected override void Configuration() => configure(RouteConfiguration(id));
    }

    /// <summary>Registers a lifecycle listener for route state change notifications.</summary>
    /// <returns>This context for fluent chaining.</returns>
    public IRouteContext AddLifecycleListener(IRouteLifecycleListener listener)
    {
        ArgumentNullException.ThrowIfNull(listener);
        lock (_lifecycleListenersLock)
            _lifecycleListeners = [.. _lifecycleListeners, listener];
        return this;
    }

    /// <inheritdoc />
    public IRouteContext AddRoutePolicyFactory(IRoutePolicyFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _policyFactories.Add(factory);
        return this;
    }

    /// <inheritdoc />
    public RoutePolicyDescriptor GetRoutePolicy(string routeId)
    {
        ArgumentNullException.ThrowIfNull(routeId);
        var route = GetRoute(routeId);
        if (route is null)
            return new RoutePolicyDescriptor(false, "AllNodes", null, "Route not found");
        return route.PolicyDescriptor;
    }

    /// <summary>
    /// Gets a compiled route by its route ID.
    /// Returns null if not found.
    /// </summary>
    public CompiledRoute? GetRoute(string routeId)
    {
        ArgumentNullException.ThrowIfNull(routeId);
        return _routes.Find(r => r.RouteId.Equals(routeId, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Starts an individual route by its route ID.
    /// The route must have been previously compiled and stopped.
    /// </summary>
    public async Task StartRoute(string routeId, CancellationToken ct = default)
    {
        await _routeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var route = GetRoute(routeId)
                ?? throw new InvalidOperationException($"Route '{routeId}' not found.");

            if (route.Status == RouteStatus.Started)
                throw new InvalidOperationException($"Route '{routeId}' is already started.");

            route.Status = RouteStatus.Starting;
            _logger?.LogInformation("Starting route '{RouteId}'...", routeId);

            try
            {
                if (route.Policy is { } policy)
                    await policy.OnStart(this, route, ct).ConfigureAwait(false);

                await route.Consumer.Start(ct).ConfigureAwait(false);
                route.Status = RouteStatus.Started;
                _logger?.LogInformation("Route '{RouteId}' started.", routeId);
                await NotifyRouteStarted(routeId, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                route.Status = RouteStatus.Errored;
                _logger?.LogError(ex, "Route '{RouteId}' failed to start.", routeId);
                await NotifyRouteErrored(routeId, ex, ct).ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            _routeLock.Release();
        }
    }

    /// <summary>
    /// Stops an individual route by its route ID.
    /// </summary>
    public Task StopRoute(string routeId, CancellationToken ct = default)
        => StopRoute(routeId, null, ct);

    /// <summary>
    /// Resumes a previously stopped/suspended route.
    /// If the route has a policy (e.g., clustered), delegates to <see cref="IRoutePolicy.OnResume"/>
    /// so the policy can restart its background lifecycle (e.g., lock acquisition loop).
    /// The consumer is NOT started directly — the policy will call <see cref="StartRoute"/> when ready.
    /// For routes without a policy, falls through to <see cref="StartRoute"/>.
    /// </summary>
    public async Task ResumeRoute(string routeId, CancellationToken ct = default)
    {
        var ownsLock = true;
        await _routeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var route = GetRoute(routeId)
                ?? throw new InvalidOperationException($"Route '{routeId}' not found.");

            if (route.Status == RouteStatus.Started)
                return;

            if (route.Policy is not { } policy)
            {
                // No policy — release lock and delegate to normal StartRoute
                ownsLock = false;
                _routeLock.Release();
                await StartRoute(routeId, ct).ConfigureAwait(false);
                return;
            }

            route.Status = RouteStatus.Starting;
            _logger?.LogInformation("Resuming route '{RouteId}' via policy...", routeId);

            try
            {
                await policy.OnResume(this, route, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                route.Status = RouteStatus.Errored;
                _logger?.LogError(ex, "Route '{RouteId}' failed to resume.", routeId);
                throw;
            }
        }
        finally
        {
            if (ownsLock)
                _routeLock.Release();
        }
    }

    /// <summary>
    /// Stops an individual route by its route ID with an optional timeout override.
    /// </summary>
    /// <param name="routeId">The route identifier.</param>
    /// <param name="timeout">Timeout for graceful drain. If null, uses <see cref="RouteEngineOptions.ShutdownTimeout"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task StopRoute(string routeId, TimeSpan? timeout, CancellationToken ct = default)
    {
        await _routeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var route = GetRoute(routeId)
                ?? throw new InvalidOperationException($"Route '{routeId}' not found.");

            if (route.Status == RouteStatus.Stopped)
                throw new InvalidOperationException($"Route '{routeId}' is already stopped.");

            var effectiveTimeout = timeout ?? _options.ShutdownTimeout;

            route.Status = RouteStatus.Suspending;
            _logger?.LogInformation("Stopping route '{RouteId}' (timeout={Timeout}s)...",
                routeId, effectiveTimeout.TotalSeconds);
            await NotifyRouteSuspending(routeId, ct).ConfigureAwait(false);

            if (route.Policy is { } policy)
            {
                try { await policy.OnSuspend(this, route, ct).ConfigureAwait(false); }
                catch (Exception ex) { _logger?.LogWarning(ex, "RoutePolicy.OnSuspend failed for route '{RouteId}'", routeId); }
            }

            route.Status = RouteStatus.Suspended;

            using var timeoutCts = new CancellationTokenSource(effectiveTimeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

            try
            {
                await route.Consumer.Stop(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
            {
                route.Status = RouteStatus.Stopping;
                _logger?.LogWarning(
                    "Route '{RouteId}' stop timed out after {Timeout}s, forced shutdown",
                    routeId, effectiveTimeout.TotalSeconds);
            }

            if (route.Policy is { } stopPolicy)
            {
                try { await stopPolicy.OnStop(this, route, ct).ConfigureAwait(false); }
                catch (Exception ex) { _logger?.LogWarning(ex, "RoutePolicy.OnStop failed for route '{RouteId}'", routeId); }
            }

            route.Status = RouteStatus.Stopped;
            _logger?.LogInformation("Route '{RouteId}' stopped.", routeId);
            await NotifyRouteStopped(routeId, ct).ConfigureAwait(false);
        }
        finally
        {
            _routeLock.Release();
        }
    }

    // ── Lifecycle ──

    /// <inheritdoc />
    public async Task Start(CancellationToken ct = default)
    {
        await _routeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_started) return;

            await NotifyContextStarting(ct).ConfigureAwait(false);
            await StartCore(ct).ConfigureAwait(false);
            await NotifyContextStarted(ct).ConfigureAwait(false);
        }
        finally
        {
            _routeLock.Release();
        }
    }

    private async Task StartCore(CancellationToken ct)
    {
        var logger = GetService<ILogger>() ?? _logger;

        if (_builders.Count > 0)
            CompileRoutesFromBuilders(logger);

        if (OnBeforeRoutesStart is not null)
            await OnBeforeRoutesStart(_routes).ConfigureAwait(false);

        if (_builders.Count > 0)
            await InitializeRoutePolicies(logger, ct).ConfigureAwait(false);

        await StartEndpointsAndConsumers(logger, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The route configurations of a route, as Apache Camel assigns them. A route without
    /// <c>.RouteConfigurationId(...)</c> gets the nameless ones — the default. A route with ids gets the configurations
    /// whose id matches one of them (an exact id, a wildcard ending in <c>*</c>, or a regular expression) and none of the
    /// nameless. An exact id no configuration has is an error: a route left without the handlers it asked for would fail
    /// in production instead of at <c>Start()</c>.
    /// </summary>
    private static List<RouteConfigurationDefinition> RouteConfigurationsOf(RouteDefinition definition, string routeId,
        List<RouteConfigurationDefinition> all)
    {
        if (definition.GetRouteConfigurationId() is not { } assigned)
            return all.Where(c => c.Id is null).ToList();

        var patterns = assigned.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var pattern in patterns)
        {
            if (IsExactConfigurationId(pattern) && !all.Any(c => c.Id == pattern))
                throw new InvalidOperationException(
                    $"Route '{routeId}': route configuration '{pattern}' is not declared in the context. " +
                    "Declare it in a RouteConfigurationBuilder (or a <routeConfiguration> of the markup) and add it before Start().");
        }
        return all.Where(c => c.Id is { } id && patterns.Any(p => MatchesConfigurationId(id, p))).ToList();
    }

    /// <summary>An id made of letters, digits, <c>_</c>, <c>-</c> and <c>.</c> only: a name, not a wildcard or a regular expression.</summary>
    private static bool IsExactConfigurationId(string pattern)
        => pattern.All(c => char.IsLetterOrDigit(c) || c is '_' or '-' or '.');

    /// <summary>Camel's pattern match: the exact id, a wildcard ending in <c>*</c>, or a regular expression over the whole id.</summary>
    private static bool MatchesConfigurationId(string id, string pattern)
    {
        if (string.Equals(id, pattern, StringComparison.Ordinal))
            return true;
        if (pattern.EndsWith('*') && id.StartsWith(pattern[..^1], StringComparison.Ordinal))
            return true;
        try
        {
            return System.Text.RegularExpressions.Regex.IsMatch(id, $"^(?:{pattern})$",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        }
        catch (ArgumentException)
        {
            return false;   // not a regular expression: it was a name, and it did not match
        }
    }

    /// <summary>
    /// Invokes all registered builders, validates definitions, compiles routes,
    /// and swaps the result into <see cref="_routes"/>.
    /// </summary>
    private void CompileRoutesFromBuilders(ILogger? logger)
    {
        var compiledRoutes = new List<CompiledRoute>();

        logger?.LogInformation("Compiling {Count} route builder(s)...", _builders.Count);

        // 1. Invoke the builders to collect definitions. AdviceWith may already have built (and
        //    rewritten) them before Start(); re-running Configure() would discard the advice. A builder
        //    added after that advice has never been built and still must be — otherwise its routes
        //    would vanish without a word.
        foreach (var builder in _builders)
        {
            if (!DefinitionsPrebuilt || !builder.IsBuilt)
                builder.InternalBuild(this);
        }

        // 2. Validate route registrations — fail fast before any compilation
        var usedFromUris = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var usedRouteIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var builder in _builders)
        {
            foreach (var definition in builder.Definitions)
            {
                var fromUri = definition.GetFromUri()
                    ?? throw new InvalidOperationException(
                        $"Route '{definition.GetRouteId() ?? "(unnamed)"}' has no From() endpoint.");
                // Expand {{key}} placeholders before the URI is parsed — dedup/identity keys, the route id
                // and the consumer endpoint (below) must all see the same resolved value.
                fromUri = ResolvePlaceholders(fromUri);

                // A ${...} in a consumer URI has no message to resolve against; before 4.0 it was silently
                // kept as text (or resolved against nothing). Fail at Start(), the same way a malformed
                // condition does — the silent variant is the class of defect already removed elsewhere.
                if (fromUri.Contains("${", StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        $"Route '{definition.GetRouteId() ?? "(unnamed)"}': the consumer URI '{EndpointUri.Sanitize(fromUri)}' contains a ${{...}} " +
                        "placeholder, but a consumer has no message to resolve it against. Use {{key}} configuration placeholders or a constant.");

                var parsedFrom = EndpointUriParser.Parse(fromUri);
                var normalizedFrom = parsedFrom.NormalizedKey;
                // Unnamed routes are named after their endpoint: scheme, path and a deterministic
                // UUID of the URI (RouteIdFactory) — no separator a dashboard label chokes on and no
                // query string a secret could ride in. Endpoint dedup below still keys on the raw
                // normalizedFrom, so identity is unaffected.
                var routeId = definition.GetRouteId() ?? RouteIdFactory.ForEndpoint(parsedFrom);

                if (!usedRouteIds.Add(routeId))
                    throw new InvalidOperationException(
                        $"Duplicate route ID '{routeId}'. Each route must have a unique ID within a context.");

                if (usedFromUris.TryGetValue(normalizedFrom, out var existingRouteId))
                    throw new InvalidOperationException(
                        $"Route '{routeId}' has the same From URI '{EndpointUri.Sanitize(fromUri)}' as route '{existingRouteId}'. " +
                        $"Two consumers on the same endpoint within one context is not allowed.");
                usedFromUris[normalizedFrom] = routeId;
            }
        }

        // 3. Builder-level OnException, Intercept* and OnCompletion apply to the routes of their own builder only
        //    (Apache Camel's RouteBuilder scope); they are attached per route below. Here only what is said once.
        foreach (var builder in _builders)
        {
            // A builder with handlers and no route of its own: the handlers apply to nothing. It is what a class (or a
            // markup file) of shared handlers becomes when it is not a route configuration — said at every start,
            // because nothing else would: the routes of the other builders simply run without those handlers.
            if (builder.Definitions.Count == 0
                && (builder.ExceptionDefinitions.Count > 0 || builder.Intercepts.Count > 0 || builder.OnCompletions.Count > 0))
            {
                logger?.LogWarning(
                    "{Builder} declares no routes, so its OnException / Intercept / OnCompletion apply to nothing: a builder's " +
                    "declarations reach the routes of that builder only. For handlers shared by the routes of other builders " +
                    "declare a route configuration — RouteConfiguration().OnException<...>() in a RouteConfigurationBuilder, " +
                    "<routeConfiguration> in markup.",
                    builder.SourceName);
            }

            foreach (var exDef in builder.ExceptionDefinitions)
            {
                foreach (var exType in exDef.ExceptionTypes)
                {
                    logger?.LogInformation(
                        "Registered builder exception handler for {ExceptionType} ({Builder})",
                        exType.Name, builder.SourceName);

                    // A handler on an OperationCanceledException can never fire — a genuine cancellation is
                    // never handled and a timeout arrives as TimeoutException. Said once at load.
                    if (Validation.RouteDefinitionValidator.CancellationHandlerWarning(exType) is { } cancellationWarning)
                        logger?.LogWarning("OnException: {Warning}", cancellationWarning);
                }
            }
        }

        // 3a. Route configurations of every builder, in the order they were added: unlike a builder's own handlers
        //     they reach the routes of other builders. An id is unique within the context.
        var routeConfigurations = _builders.SelectMany(b => b.RouteConfigurations).ToList();
        var configurationIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var configuration in routeConfigurations)
        {
            if (configuration.Id is { } configurationId && !configurationIds.Add(configurationId))
                throw new InvalidOperationException(
                    $"Duplicate route configuration id '{configurationId}'. A route configuration id is unique within a context.");

            foreach (var exType in configuration.ExceptionDefinitions.SelectMany(d => d.ExceptionTypes))
            {
                logger?.LogInformation(
                    "Registered route configuration exception handler for {ExceptionType} ({Configuration})",
                    exType.Name, configuration.Id ?? "default");
                if (Validation.RouteDefinitionValidator.CancellationHandlerWarning(exType) is { } cancellationWarning)
                    logger?.LogWarning("OnException: {Warning}", cancellationWarning);
            }
        }

        // 4. Validate route definitions before compilation
        foreach (var builder in _builders)
        {
            foreach (var definition in builder.Definitions)
            {
                var warnings = Validation.RouteDefinitionValidator.Validate(definition);
                foreach (var warning in warnings)
                    logger?.LogWarning("Route '{RouteId}': {Warning}", definition.GetRouteId() ?? "<unnamed>", warning);
            }
        }

        // 5. Compile each route definition
        _checkpoints.Clear();   // rebuilt from scratch on every (re)compile
        _errorScopes.Clear();
        foreach (var builder in _builders)
        {
            foreach (var definition in builder.Definitions)
            {
                var fromUri = ResolvePlaceholders(definition.GetFromUri()!);
                // Unnamed routes are named after their endpoint (see RouteIdFactory). Kept in sync
                // with the validation loop above; endpoint identity/dedup still keys on the raw
                // normalizedFrom.
                var routeId = definition.GetRouteId()
                    ?? RouteIdFactory.ForEndpoint(EndpointUriParser.Parse(fromUri));

                PipelineProcessor pipeline;
                // The route's error scope: its own OnException (added while its body compiles), its builder's — that
                // builder's only —, those of its route configurations, and the context layer.
                var errorScope = new RouteErrorScope(this, routeId, builder.ExceptionDefinitions,
                    RouteConfigurationsOf(definition, routeId, routeConfigurations)
                        .SelectMany(c => c.ExceptionDefinitions).ToList(),
                    _loggerFactory?.CreateLogger<OnExceptionProcessor>());
                _errorScopes[routeId] = errorScope;
                _compilingErrorScope = errorScope;
                _compilingRouteId = routeId;   // so nested ReplayableDefinition registers under this route
                // Route-level .MessageHistory() overrides the global option; each route restarts node numbering.
                _compilingMessageHistory = definition.GetMessageHistory() ?? _options.EnableMessageHistory;
                _compilingNodeCounter = 0;
                // Intercepts of this route: its builder's plus the route's own; CompileNode decorates every step with them.
                _compilingIntercepts = builder.Intercepts.Concat(definition.GetIntercepts()).ToList();
                _interceptBodies.Clear();
                List<CompletionHandler> completionHandlers = [];
                _compilingStaticEndpoints = [];
                _compilingSkippedStaticEndpoints = new HashSet<string>(StringComparer.Ordinal);
                try
                {
                    var inner = definition.CreateProcessor(this);
                    inner = ApplyInterceptFrom(inner, fromUri);
                    pipeline = inner as PipelineProcessor ?? new PipelineProcessor();
                    if (inner is not PipelineProcessor)
                        pipeline.Add(inner);

                    // OnCompletion bodies compile here, inside the route's compile frame (route id for
                    // checkpoints, message history), detached from its intercepts; they are attached
                    // further down, outside every error handler.
                    completionHandlers = builder.OnCompletions.Concat(definition.GetOnCompletions())
                        .Select(c => new CompletionHandler(CompileDetached(c.Outputs), c.Mode, c.Condition, c.BeforeConsumer))
                        .ToList();

                    // The static targets' endpoints, now: a bad URI fails here like any compile error, not on the
                    // first message. Only the endpoint — its producer is made when a message needs it.
                    foreach (var staticUri in _compilingStaticEndpoints)
                        if (!_compilingSkippedStaticEndpoints.Contains(ResolvePlaceholders(staticUri)))
                            GetEndpoint(staticUri);
                }
                catch (Exception ex) when (!_options.ThrowOnCompilationError)
                {
                    logger?.LogError(ex, "Failed to compile route '{RouteId}'. Skipping.", routeId);
                    // A skipped route keeps no error scope.
                    _errorScopes.TryRemove(routeId, out _);
                    continue;
                }
                finally
                {
                    _compilingErrorScope = null;
                    _compilingStaticEndpoints = null;
                    _compilingSkippedStaticEndpoints = null;
                    _compilingRouteId = null;
                    _compilingMessageHistory = false;
                    _compilingIntercepts = [];
                    _interceptBodies.Clear();
                }

                // Wrap with routeId stamp
                var wrappedPipeline = new PipelineProcessor();
                wrappedPipeline.Add(new DelegateProcessor(exchange => exchange.RouteId = routeId));
                // RouteEngineOptions.StreamCaching.Enabled, Camel's context-wide streamCaching: every route caches a
                // stream body before its first step, so any step can read it again.
                if (_options.StreamCaching.Enabled)
                    wrappedPipeline.Add(new StreamCachingTransformer(Extensions.RouteContextExtensions.GetStreamCacheOptions(this)));
                wrappedPipeline.AddRange(pipeline.Processors);

                // Resolve route policy: explicit definition wins, then a registry name
                // (Route-XML Ф1.3), then the factory chain.
                IRoutePolicy? routePolicy = definition.GetRoutePolicy();
                if (routePolicy is null && definition.GetRoutePolicyName() is { } policyName)
                {
                    routePolicy = GetFromRegistry<IRoutePolicy>(policyName)
                        ?? throw new InvalidOperationException(
                            $"Route '{routeId}': route policy '{policyName}' is not in the context registry. " +
                            "Register it with AddToRegistry before Start().");
                }
                IRoutePolicyFactory? winningFactory = null;
                var explicitPolicy = routePolicy is not null;
                if (routePolicy is null)
                {
                    foreach (var factory in _policyFactories)
                    {
                        routePolicy = factory.CreateRoutePolicy(this, routeId, definition);
                        if (routePolicy is not null) { winningFactory = factory; break; }
                    }
                }

                // Build the policy descriptor (one source of truth for diagnostics + API)
                var requestedCluster = definition.GetCluster();
                RoutePolicyDescriptor descriptor;
                if (explicitPolicy)
                {
                    descriptor = new RoutePolicyDescriptor(
                        requestedCluster, "Custom", routePolicy!.GetType().FullName,
                        "Explicit RoutePolicy attached on the route definition");
                }
                else if (routePolicy is not null)
                {
                    descriptor = new RoutePolicyDescriptor(
                        requestedCluster, "ClusterLeader", winningFactory!.GetType().FullName,
                        $"Policy supplied by {winningFactory.GetType().Name}");
                }
                else
                {
                    // A registered factory may decline a route (IRoutePolicyFactory returns null) and say why itself.
                    var reason = !requestedCluster
                        ? "No policy attached — runs on all nodes"
                        : _policyFactories.Count == 0
                            ? "Route requested .Cluster(true) but no IRoutePolicyFactory is registered — running on all nodes"
                            : "Route requested .Cluster(true) but the registered IRoutePolicyFactory declined it — running on all nodes";
                    descriptor = new RoutePolicyDescriptor(requestedCluster, "AllNodes", null, reason);
                }

                // Startup safety: warn when .Cluster(true) is requested without any factory
                if (_options.StartupChecks && requestedCluster && routePolicy is null && _policyFactories.Count == 0)
                {
                    logger?.LogWarning(
                        "Route '{RouteId}' declares .Cluster(true) but no IRoutePolicyFactory is registered. " +
                        "The route will execute on every node. Register a clustered route policy factory " +
                        "(e.g. Tsak's ClusteredRoutePolicyFactory) or set RouteEngineOptions.StartupChecks=false to silence.",
                        routeId);
                }

                // Wrap with RoutePolicyProcessor for per-exchange hooks
                if (routePolicy is not null)
                {
                    var rpProcessor = new RoutePolicyProcessor(wrappedPipeline, routePolicy, this);
                    wrappedPipeline = new PipelineProcessor();
                    wrappedPipeline.Add(rpProcessor);
                }

                // Apply per-exchange processing timeout
                IProcessor finalProcessor = wrappedPipeline;
                var routeTimeout = definition.GetProcessingTimeout() ?? _options.DefaultProcessingTimeout;
                if (routeTimeout != Timeout.InfiniteTimeSpan)
                    finalProcessor = new TimeoutProcessor(finalProcessor, routeTimeout, routeId, this,
                        _loggerFactory?.CreateLogger<TimeoutProcessor>());

                // The rest of the route's error scope, around the route policy and the timeout: the OnException blocks
                // of this route's builder (that builder's only — Camel's RouteBuilder scope), then the context's error
                // handling for a failure no OnException took. Also completes the detached bodies of the route.
                finalProcessor = errorScope.WrapOuter(finalProcessor);

                // Telemetry / metrics: outside every error handler, as the statistics below, so a redelivered exchange is
                // one span and one measurement with its final outcome — not one per attempt. Route-level .Tracing()
                // overrides the global option, so a service route (a health check, a metric summary) opens no span.
                var routeTracing = definition.GetTracing() ?? _options.EnableTelemetry;
                if (routeTracing)
                    finalProcessor = new InstrumentedProcessor(finalProcessor, $"route:{routeId}");

                if (_options.EnableMetrics)
                {
                    var endpointScheme = EndpointUriParser.Parse(fromUri).Scheme;
                    finalProcessor = new MeteredProcessor(finalProcessor, routeId, EndpointUri.Sanitize(fromUri), endpointScheme);
                }

                // OnCompletion blocks: outside every error handler, so "failure" is the escaping exception
                // and a handled error is a completion. Their bodies compile detached (no intercepts).
                if (completionHandlers.Count > 0)
                    finalProcessor = new OnCompletionProcessor(finalProcessor, completionHandlers, routeId, _loggerFactory?.CreateLogger<OnCompletionProcessor>());

                // The exchange's unit of work: outside every error handler, inside the statistics, inflight tracking and
                // exchange events, so a completion (an idempotent key confirmed or removed) has run before the consumer
                // settles the message and before a listener hears the exchange finished.
                finalProcessor = new UnitOfWorkProcessor(finalProcessor, _loggerFactory?.CreateLogger<UnitOfWorkProcessor>());

                // Create consumer on the From endpoint
                var endpoint = GetEndpoint(fromUri);
                finalProcessor = new StatisticsProcessor(finalProcessor, endpoint);

                // Outermost wrapper: inflight tracking
                finalProcessor = new InflightTrackingProcessor(finalProcessor, _inflightRepository, routeId, EndpointUri.Sanitize(fromUri));

                // Exchange-level lifecycle events (received / completed / failed) for listeners such as
                // NotifyBuilder — outside every error handler so the reported outcome is the final one.
                finalProcessor = new ExchangeEventsProcessor(finalProcessor, this, routeId);

                // Outermost of all: IExchange.Context points at the context whose route is
                // executing, for exactly as long as it is executing — so even lifecycle listeners
                // see a stamped exchange, and the caller gets its own view back afterwards.
                // Beside it, the inbound span of the transport, for .SpanTag(..., SpanTarget.Inbound): on entry it is the
                // ambient activity, opened by the consumer around this pipeline. No inbound span opens with telemetry off.
                if (_options.EnableTelemetry)
                    finalProcessor = new InboundSpanProcessor(finalProcessor);
                finalProcessor = new ContextStampProcessor(finalProcessor, this);

                var consumer = endpoint.CreateConsumer(finalProcessor);
                AddConsumer(consumer);

                compiledRoutes.Add(new CompiledRoute(routeId, EndpointUri.Sanitize(fromUri), definition, wrappedPipeline, consumer, endpoint)
                {
                    AutoStart = definition.GetAutoStart(),
                    Policy = routePolicy,
                    PolicyDescriptor = descriptor
                });

                logger?.LogInformation("Compiled route '{RouteId}': {FromUri}", routeId, EndpointUri.Sanitize(fromUri));
            }
        }

        // All routes compiled successfully — swap in atomically
        _routes.Clear();
        _routes.AddRange(compiledRoutes);
    }

    /// <summary>
    /// Calls <see cref="IRoutePolicy.OnInit"/> on each route that has a policy.
    /// Must run after <see cref="OnBeforeRoutesStart"/> so persisted suspended state
    /// is already applied to <see cref="CompiledRoute.AutoStart"/>.
    /// </summary>
    private async Task InitializeRoutePolicies(ILogger? logger, CancellationToken ct)
    {
        foreach (var route in _routes)
        {
            if (route.Policy is { } policy)
            {
                try { await policy.OnInit(this, route, ct).ConfigureAwait(false); }
                catch (Exception ex)
                {
                    logger?.LogError(ex, "RoutePolicy.OnInit failed for route '{RouteId}'", route.RouteId);
                    route.Status = RouteStatus.Errored;
                }
            }
        }
    }

    /// <summary>
    /// Starts endpoints, then consumers (in-process first, external second),
    /// skipping routes with <see cref="CompiledRoute.AutoStart"/> = false.
    /// Marks each route's <see cref="CompiledRoute.Status"/> accordingly.
    /// </summary>
    private async Task StartEndpointsAndConsumers(ILogger? logger, CancellationToken ct)
    {
        var failedEndpoints = new List<(string Uri, Exception Error)>();
        var startedCount = 0;

        // Start endpoints
        foreach (var (key, endpoint) in _endpoints)
        {
            try
            {
                await endpoint.Start(ct).ConfigureAwait(false);
                startedCount++;
                logger?.LogInformation("Endpoint {Uri} started successfully", EndpointUri.Sanitize(key));
            }
            catch (Exception ex)
            {
                failedEndpoints.Add((key, ex));
                logger?.LogError(ex, "Failed to start endpoint {Uri}. Context will continue with remaining endpoints.", EndpointUri.Sanitize(key));
            }
        }

        // Start in-process consumers first (Direct, Seda) so their processors are registered
        // before external consumers (Kafka, Timer, etc.) begin sending messages.
        // Skip consumers for routes with AutoStart=false.
        var skippedConsumers = new HashSet<IConsumer>(
            _routes.Where(r => !r.AutoStart).Select(r => r.Consumer));

        var inProcess = _consumers.Where(c => IsInProcessConsumer(c) && !skippedConsumers.Contains(c));
        var external = _consumers.Where(c => !IsInProcessConsumer(c) && !skippedConsumers.Contains(c));
        var failedConsumers = new HashSet<IConsumer>();

        foreach (var consumer in inProcess)
        {
            try
            {
                await consumer.Start(ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                failedConsumers.Add(consumer);
                logger?.LogError(ex, "Failed to start in-process consumer. Context will continue with remaining consumers.");
            }
        }

        foreach (var consumer in external)
        {
            try
            {
                await consumer.Start(ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                failedConsumers.Add(consumer);
                logger?.LogError(ex, "Failed to start consumer. Context will continue with remaining consumers.");
            }
        }

        _started = true;

        // Mark compiled routes based on their AutoStart flag
        foreach (var route in _routes)
        {
            if (failedConsumers.Contains(route.Consumer))
            {
                route.Status = RouteStatus.Errored;
                logger?.LogWarning("Route '{RouteId}' marked as Errored due to consumer start failure", route.RouteId);
                await NotifyRouteErrored(route.RouteId,
                    new InvalidOperationException($"Consumer failed to start for route '{route.RouteId}'"), ct)
                    .ConfigureAwait(false);
            }
            else if (route.AutoStart)
            {
                if (route.Policy is { } startPolicy)
                {
                    try { await startPolicy.OnStart(this, route, ct).ConfigureAwait(false); }
                    catch (Exception ex)
                    {
                        route.Status = RouteStatus.Errored;
                        logger?.LogError(ex, "RoutePolicy.OnStart failed for route '{RouteId}'", route.RouteId);
                        await NotifyRouteErrored(route.RouteId, ex, ct).ConfigureAwait(false);
                        continue;
                    }
                }

                route.Status = RouteStatus.Started;
                await NotifyRouteStarted(route.RouteId, ct).ConfigureAwait(false);
            }
            else
            {
                route.Status = RouteStatus.Stopped;
                logger?.LogInformation("Route '{RouteId}' has AutoStart=false, remaining stopped", route.RouteId);
            }
        }

        if (failedEndpoints.Count > 0)
        {
            var failedUris = string.Join(", ", failedEndpoints.Select(f => EndpointUri.Sanitize(f.Uri)));
            logger?.LogError(
                "Context '{ContextId}' started in DEGRADED mode: success={SuccessCount}, FAILED={FailedCount}. " +
                "Non-operational endpoints: {FailedEndpoints}. IMMEDIATE ATTENTION REQUIRED!",
                ContextId, startedCount, failedEndpoints.Count, failedUris);
        }
        else
        {
            logger?.LogInformation("Context '{ContextId}' started successfully: all {Count} endpoints operational",
                ContextId, startedCount);
        }
    }

    /// <inheritdoc />
    public async Task Stop(CancellationToken ct = default)
    {
        await _routeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_started) return;

            await NotifyContextStopping(ct).ConfigureAwait(false);

            // Mark all active routes as Suspending → Suspended
            foreach (var route in _routes)
            {
                if (route.Status == RouteStatus.Started)
                {
                    route.Status = RouteStatus.Suspending;
                    await NotifyRouteSuspending(route.RouteId, ct).ConfigureAwait(false);

                    if (route.Policy is { } suspendPolicy)
                    {
                        try { await suspendPolicy.OnSuspend(this, route, ct).ConfigureAwait(false); }
                        catch (Exception ex) { _logger?.LogWarning(ex, "RoutePolicy.OnSuspend failed for route '{RouteId}'", route.RouteId); }
                    }

                    route.Status = RouteStatus.Suspended;
                }
            }

            // Apply ShutdownTimeout — gives consumers a bounded window to drain in-flight work.
            using var timeoutCts = new CancellationTokenSource(_options.ShutdownTimeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            var stopCt = linked.Token;

            // Shutdown mirrors Start order (in-process first, external last) in reverse.
            // Dependencies: external → To("kafka:") needs producer alive
            //               external → To("direct:") needs Direct alive
            //               seda drain → To("direct:") needs Direct alive
            //               seda drain → To("kafka:") needs producer alive — NO: producers flush after externals

            var external = _consumers.Where(c => !IsInProcessConsumer(c)).ToList();
            var queued = _consumers.Where(IsQueueConsumer).ToList();
            var direct = _consumers.Where(IsDirectConsumer).ToList();

            // Phase 1: Stop external consumers in parallel (Kafka, Timer, Quartz, etc.)
            // Each Stop() internally does: stop-accepting → drain-inflight → cleanup.
            // During drain, producers and Direct/Seda are still alive for .To() calls.
            await StopAllAsync(external, c => c.Stop(stopCt), c => c.GetType().Name, "Consumer", timeoutCts, ct)
                .ConfigureAwait(false);

            // Phase 1b: processors holding exchanges of their own (open aggregation groups) settle them: nothing more
            // arrives from outside, and the producers are still alive for what a completion sends on.
            foreach (var holder in _disposableProcessors.OfType<IStopAwareProcessor>().ToArray())
            {
                try
                {
                    await holder.OnConsumersStoppedAsync(stopCt).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    _logger?.LogError(ex, "Settling {Processor} on stop failed", holder.GetType().Name);
                }
            }

            // Phase 2: Stop producers in parallel (flush pending sends, close connections).
            // No more inflight exchanges from external consumers, safe to tear down.
            IProducer[] producers;
            lock (_producersLock) producers = [.. _producers];
            await StopAllAsync(producers, p => p.Stop(stopCt), p => p.GetType().Name, "Producer", timeoutCts, ct)
                .ConfigureAwait(false);

            // Phase 3: Drain Seda/Vm queues in parallel.
            // External consumers may have enqueued work via .To("seda://").
            // During drain, Direct processors are still registered.
            await StopAllAsync(queued, c => c.Stop(stopCt), c => c.GetType().Name, "Consumer", timeoutCts, ct)
                .ConfigureAwait(false);

            // Phase 4: Unregister Direct/DirectVm processors (instant, no drain).
            // All senders are stopped — no one will call these anymore.
            await StopAllAsync(direct, c => c.Stop(stopCt), c => c.GetType().Name, "Consumer", timeoutCts, ct)
                .ConfigureAwait(false);

            foreach (var endpoint in _endpoints.Values)
                await endpoint.Stop(ct).ConfigureAwait(false);

            foreach (var route in _routes)
            {
                if (route.Policy is { } stopPolicy)
                {
                    try { await stopPolicy.OnStop(this, route, ct).ConfigureAwait(false); }
                    catch (Exception ex) { _logger?.LogWarning(ex, "RoutePolicy.OnStop failed for route '{RouteId}'", route.RouteId); }
                }

                route.Status = RouteStatus.Stopped;
                await NotifyRouteStopped(route.RouteId, ct).ConfigureAwait(false);
            }

            // Notify policies that routes are being removed
            foreach (var route in _routes)
            {
                if (route.Policy is { } policy)
                {
                    try { await policy.OnRemove(this, route, ct).ConfigureAwait(false); }
                    catch (Exception ex) { _logger?.LogWarning(ex, "RoutePolicy.OnRemove failed for route '{RouteId}'", route.RouteId); }
                }
            }

            // Clear all runtime resources including compiled routes.
            // Routes are recompiled from _builders on next Start().
            ClearRuntimeState();
            _routes.Clear();

            _started = false;

            await NotifyContextStopped(ct).ConfigureAwait(false);
        }
        finally
        {
            _routeLock.Release();
        }
    }

    /// <summary>Registers a consumer to be started/stopped with the context.</summary>
    /// <param name="consumer">Consumer instance.</param>
    internal void AddConsumer(IConsumer consumer) => _consumers.Add(consumer);

    /// <summary>Adds a compiled route directly (for testing).</summary>
    internal void AddRoute(CompiledRoute route) => _routes.Add(route);

    /// <summary>Tracks a producer so it is stopped during context shutdown.</summary>
    /// <param name="producer">Producer instance.</param>
    internal void TrackProducer(IProducer producer)
    {
        lock (_producersLock) _producers.Add(producer);
    }

    /// <summary>
    /// Takes a producer off the shutdown list, for an owner that stops it itself (a producer template). <c>false</c>
    /// when the producer is not on it: the context has stopped it already, or never tracked it.
    /// </summary>
    /// <param name="producer">Producer instance.</param>
    internal bool UntrackProducer(IProducer producer)
    {
        lock (_producersLock) return _producers.Remove(producer);
    }

    /// <inheritdoc />
    public IReadOnlyList<IEndpoint> GetEndpoints() => _endpoints.Values.ToList();

    /// <summary>In-process consumers (Direct, Seda, DirectVm, Vm) that must start first and stop last.</summary>
    private static bool IsInProcessConsumer(IConsumer consumer) =>
        consumer is Components.DirectConsumer or Components.SedaConsumer
            or Components.DirectVmConsumer or Components.VmConsumer;

    /// <summary>Queue-based in-process consumers (Seda, Vm) that may accumulate work from external consumers.</summary>
    private static bool IsQueueConsumer(IConsumer consumer) =>
        consumer is Components.SedaConsumer or Components.VmConsumer;

    /// <summary>Synchronous in-process consumers (Direct, DirectVm) that only unregister a processor.</summary>
    private static bool IsDirectConsumer(IConsumer consumer) =>
        consumer is Components.DirectConsumer or Components.DirectVmConsumer;

    /// <summary>
    /// Stops all items in parallel. Each item runs its own stop logic concurrently;
    /// timeout exceptions are caught per-item without aborting siblings.
    /// </summary>
    private async Task StopAllAsync<T>(
        IEnumerable<T> items,
        Func<T, Task> stopAction,
        Func<T, string> nameSelector,
        string kind,
        CancellationTokenSource timeoutCts,
        CancellationToken outerCt)
    {
        var tasks = items.Select(async item =>
        {
            try
            {
                await stopAction(item).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !outerCt.IsCancellationRequested)
            {
                _logger?.LogWarning(
                    "{Kind} {Name} stop timed out after {Timeout}s, forcing shutdown",
                    kind, nameSelector(item), _options.ShutdownTimeout.TotalSeconds);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex,
                    "{Kind} {Name} stop failed, continuing shutdown",
                    kind, nameSelector(item));
            }
        });

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    // ── IDisposable / IAsyncDisposable ──

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_started)
        {
            await Stop(CancellationToken.None).ConfigureAwait(false);
        }

        await DisposeProcessorsAsync().ConfigureAwait(false);
        await DisposeComponentsAsync().ConfigureAwait(false);

        ClearAll();
        GC.SuppressFinalize(this);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_started)
        {
            Stop(CancellationToken.None).GetAwaiter().GetResult();
        }

        DisposeProcessorsAsync().AsTask().GetAwaiter().GetResult();
        DisposeComponentsAsync().AsTask().GetAwaiter().GetResult();

        ClearAll();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Releases processors that own background resources (aggregation/resequencing timers),
    /// registered by <see cref="CompileNode"/>. Runs before component disposal — a processor may
    /// still hold component-provided resources. Failures are logged and do not stop the sweep.
    /// </summary>
    private async ValueTask DisposeProcessorsAsync()
    {
        foreach (var processor in _disposableProcessors)
        {
            try
            {
                await processor.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Error disposing processor {Processor}", processor.GetType().Name);
            }
        }
        _disposableProcessors.Clear();
    }

    /// <summary>
    /// Releases component-owned resources (connection pools, broker sessions, etc.).
    /// Each component's <see cref="ComponentBase.DisposeAsync"/> is awaited; failures are logged
    /// but do not prevent disposal of the remaining components.
    /// </summary>
    private async ValueTask DisposeComponentsAsync()
    {
        // Distinct: a component may be registered under both its primary scheme
        // and one or more aliases — the same instance must only be disposed once.
        var seen = new HashSet<IComponent>(ReferenceEqualityComparer.Instance);
        foreach (var component in _components.Values)
        {
            if (!seen.Add(component)) continue;
            if (component is not IAsyncDisposable disposable) continue;

            try
            {
                await disposable.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Error disposing component {Scheme}", component.Scheme);
            }
        }
    }

    private void ClearRuntimeState()
    {
        _consumers.Clear();
        _endpoints.Clear();
        lock (_producersLock) _producers.Clear();
        // Only what the compiler derived; the handlers the user registered are configuration, like the components,
        // and a stopped context that is started again must still have them.
        _errorScopes.Clear();
    }

    private void ClearAll()
    {
        ClearRuntimeState();
        _globalExceptionHandlers.Clear();
        _localExceptionHandlers.Clear();
        _routes.Clear();
        _components.Clear();
        _properties.Clear();
        _registry.Clear();
        _services.Clear();
        _builders.Clear();
        _discoveredBuilderTypes.Clear();
    }

    // ── Private helpers ──

    /// <summary>
    /// Compiles a single child definition into its runtime processor. The one seam every scope's body
    /// compilation flows through (via <see cref="Definitions.NodePipeline"/>), so per-node compile-time
    /// decoration has exactly one place to live. When message history is enabled for the route being
    /// compiled, the node is wrapped in a <see cref="MessageHistoryProcessor"/>.
    /// </summary>
    internal IProcessor CompileNode(IProcessorDefinition definition)
    {
        var inner = definition.CreateProcessor(this);
        if (inner is null)
            return inner;

        // Review Ф1 (2026-09-02): processors owning background resources (the aggregator's and
        // resequencer's timers) declare IAsyncDisposable, but nothing ever disposed them — the
        // resequencer's DisposeAsync was dead code and the aggregator's periodic scan timer
        // outlived the context. This seam is the one place every compiled node flows through,
        // so ownership is registered here and released when the context is disposed (not on
        // Stop(): a stopped context may be started again and reuses its compiled processors).
        if (inner is IAsyncDisposable disposable)
            _disposableProcessors.Add(disposable);

        inner = ApplyIntercepts(definition, inner);
        if (!_compilingMessageHistory)
            return inner;

        // The counter is consumed from the TYPE-derived label regardless of StepId/StepDescription,
        // so naming or describing one node never shifts the generated ids of its neighbours.
        var typeLabel = MessageHistoryLabel(definition);
        var sequence = ++_compilingNodeCounter;
        var step = definition as ProcessorDefinition;
        var label = step?.StepDescription ?? typeLabel;
        var nodeId = step?.StepId ?? typeLabel + sequence;
        return new MessageHistoryProcessor(inner, _compilingRouteId ?? "route", nodeId, label);
    }

    // ── Intercepts (phase 07): decorators applied per node at compile time ──────────────

    private IReadOnlyList<InterceptDefinition> _compilingIntercepts = [];
    private readonly Dictionary<InterceptDefinition, IProcessor> _interceptBodies = new(ReferenceEqualityComparer.Instance);
    private int _detachedCompileDepth;

    /// <summary>Wraps a compiled step with the route's intercepts: send-interception innermost, then every-step interception.</summary>
    private IProcessor ApplyIntercepts(IProcessorDefinition definition, IProcessor inner)
    {
        if (_compilingIntercepts.Count == 0 || _detachedCompileDepth > 0)
            return inner;

        // A static To may carry {{key}} configuration placeholders: the mask (and the published target)
        // must see the resolved URI, the one the endpoint is created from. A ToD's own resolver is shared
        // with its wrappers, so the target is evaluated once per message and the send goes where the
        // wrapper looked (a non-pure expression would otherwise publish one URI and send to another).
        var staticUri = definition is ToDefinition to ? ResolvePlaceholders(to.Uri) : null;
        var dynamicResolver = (inner as ToDynamicProcessor)?.Resolver;

        // Wrapped last is outermost and runs first, so the list is walked in reverse: the first
        // declared intercept (builder-level ones first) is the outermost and runs first.
        foreach (var intercept in _compilingIntercepts.Reverse())
        {
            if (intercept.Kind != InterceptKind.SendToEndpoint) continue;
            Func<IExchange, string>? target = staticUri is not null ? _ => staticUri
                : dynamicResolver is not null ? dynamicResolver.ResolveUri
                : definition is ToDynamicDefinition dynamic ? dynamic.CreateResolver(this).ResolveUri
                : null;
            if (target is null) continue;
            if (staticUri is not null && !UriMask.IsMatch(intercept.UriPattern!, staticUri)) continue;
            if (staticUri is not null && intercept.SkipsOriginal)
                _compilingSkippedStaticEndpoints?.Add(staticUri);
            inner = new InterceptSendProcessor(InterceptBody(intercept), intercept.Condition, intercept.SkipsOriginal, intercept.UriPattern!, target, inner, dynamicResolver);
        }

        // Wrapped last is outermost and runs first, so the list is walked in reverse: the first
        // declared intercept (builder-level ones first) is the outermost and runs first.
        foreach (var intercept in _compilingIntercepts.Reverse())
        {
            if (intercept.Kind != InterceptKind.EveryStep) continue;
            inner = new InterceptProcessor(InterceptBody(intercept), intercept.Condition, inner);
        }
        return inner;
    }

    /// <summary>Wraps the route pipeline with the <c>InterceptFrom</c> blocks whose mask matches the consumer URI.</summary>
    private IProcessor ApplyInterceptFrom(IProcessor pipeline, string fromUri)
    {
        // Wrapped last is outermost and runs first, so the list is walked in reverse: the first
        // declared intercept (builder-level ones first) is the outermost and runs first.
        foreach (var intercept in _compilingIntercepts.Reverse())
        {
            if (intercept.Kind != InterceptKind.From) continue;
            if (intercept.UriPattern is not null && !UriMask.IsMatch(intercept.UriPattern, fromUri)) continue;
            pipeline = new InterceptProcessor(InterceptBody(intercept), intercept.Condition, pipeline);
        }
        return pipeline;
    }

    /// <summary>The intercept's own steps, compiled once per route and never intercepted themselves.</summary>
    private IProcessor InterceptBody(InterceptDefinition intercept)
    {
        if (!_interceptBodies.TryGetValue(intercept, out var body))
        {
            body = CompileDetached(intercept.Outputs);
            _interceptBodies[intercept] = body;
        }
        return body;
    }

    /// <summary>Compiles steps outside the route's interception (intercept bodies, OnCompletion bodies).</summary>
    private IProcessor CompileDetached(IList<IProcessorDefinition> outputs)
    {
        _detachedCompileDepth++;
        try { return NodePipeline.Body(this, outputs); }
        finally { _detachedCompileDepth--; }
    }

    /// <summary>Short node label from the definition type (e.g. <c>ToDefinition</c> → <c>to</c>).</summary>
    private static string MessageHistoryLabel(IProcessorDefinition definition)
    {
        var name = definition.GetType().Name;
        const string suffix = "Definition";
        if (name.EndsWith(suffix, StringComparison.Ordinal))
            name = name[..^suffix.Length];
        return name.Length == 0 ? "node" : char.ToLowerInvariant(name[0]) + name[1..];
    }

    private IEndpoint CreateEndpoint(EndpointUri parsed)
    {
        if (!_components.TryGetValue(parsed.Scheme, out var component))
            throw new InvalidOperationException(
                $"No component registered for scheme '{parsed.Scheme}'. " +
                $"Available schemes: {string.Join(", ", _components.Keys)}");

        var endpoint = component.CreateEndpoint(parsed);

        // Propagate scope factory for per-exchange DI scope support
        if (_serviceProvider != null)
            endpoint.ScopeFactory = _serviceProvider.GetService<IServiceScopeFactory>();

        return endpoint;
    }

    /// <summary>
    /// Walks the exception type hierarchy to find the most specific matching handler.
    /// </summary>
    private static IProcessor? FindHandler(
        IDictionary<Type, IProcessor> handlers,
        Type exceptionType)
    {
        var searchType = exceptionType;
        while (searchType is not null && searchType != typeof(object))
        {
            if (handlers.TryGetValue(searchType, out var processor))
                return processor;
            searchType = searchType.BaseType;
        }

        return null;
    }

    /// <summary>
    /// The error scope a detached body compiled now is wrapped by (<c>.Threads()</c> for an in-only exchange): the
    /// scope of the route being compiled, or <c>null</c> outside a route, inside a handler body and inside a detached
    /// block (<c>OnCompletion</c>) — those run outside the route's error handling.
    /// </summary>
    internal RouteErrorScope? DetachedErrorScope => _detachedCompileDepth > 0 ? null : _compilingErrorScope;

    /// <summary>
    /// Compiles the steps of an <c>OnException</c> handler for <see cref="RouteErrorScope"/>. A detached body inside a
    /// handler is not wrapped by the route's scope again: a failure of the handler is not handled by the handler.
    /// </summary>
    internal IProcessor CompileHandlerBody(IList<IProcessorDefinition> outputs)
    {
        var scope = _compilingErrorScope;
        _compilingErrorScope = null;
        try { return TryCatchDefinition.BuildPipeline(outputs, this); }
        finally { _compilingErrorScope = scope; }
    }

    // ── Lifecycle listener notifications ──
    //
    // Policy:
    //   * OnContextStarting → fail-fast. This is the only pre-initialization hook where
    //     listeners perform work required for correctness (seed DataProtection key-ring,
    //     sync EAV schemas, etc.). If it fails, the system is half-initialized and must
    //     NOT accept traffic — so we aggregate errors across listeners and re-throw.
    //   * All other phases (OnContextStarted/Stopping/Stopped, OnRouteStarted/Stopped/
    //     Suspending/Errored, OnExchangeTimedOut) → resilient. These are post-factum
    //     observers. A broken observer must not:
    //       - abort the rest of Start()/Stop() (would leak consumers/producers/connections
    //         on teardown);
    //       - propagate into caller code that only wanted to "notify about a started route".
    //     Errors are logged at Error level (up from the old LogWarning — which hid real
    //     bootstrap bugs silently) and swallowed so downstream listeners + cleanup continue.

    private async Task InvokeListenersAsync(
        string phase,
        Func<IRouteLifecycleListener, Task> invoker,
        bool failFast)
    {
        List<Exception>? errors = null;
        foreach (var listener in Volatile.Read(ref _lifecycleListeners))
        {
            try { await invoker(listener).ConfigureAwait(false); }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Lifecycle listener {Listener} failed in {Phase}", listener.GetType().FullName, phase);
                if (failFast) (errors ??= new List<Exception>()).Add(ex);
            }
        }
        if (errors is { Count: > 0 })
            throw new AggregateException($"One or more lifecycle listeners failed in {phase}", errors);
    }

    // Sole fail-fast phase — bootstrap.
    private Task NotifyContextStarting(CancellationToken ct) =>
        InvokeListenersAsync(nameof(IRouteLifecycleListener.OnContextStarting),
            l => l.OnContextStarting(this, ct), failFast: true);

    // All other phases are post-factum observers — resilient (log-and-continue).
    private Task NotifyContextStarted(CancellationToken ct) =>
        InvokeListenersAsync(nameof(IRouteLifecycleListener.OnContextStarted),
            l => l.OnContextStarted(this, ct), failFast: false);

    private Task NotifyContextStopping(CancellationToken ct) =>
        InvokeListenersAsync(nameof(IRouteLifecycleListener.OnContextStopping),
            l => l.OnContextStopping(this, ct), failFast: false);

    private Task NotifyContextStopped(CancellationToken ct) =>
        InvokeListenersAsync(nameof(IRouteLifecycleListener.OnContextStopped),
            l => l.OnContextStopped(this, ct), failFast: false);

    private Task NotifyRouteStarted(string routeId, CancellationToken ct) =>
        InvokeListenersAsync(nameof(IRouteLifecycleListener.OnRouteStarted),
            l => l.OnRouteStarted(routeId, ct), failFast: false);

    private Task NotifyRouteStopped(string routeId, CancellationToken ct) =>
        InvokeListenersAsync(nameof(IRouteLifecycleListener.OnRouteStopped),
            l => l.OnRouteStopped(routeId, ct), failFast: false);

    private Task NotifyRouteSuspending(string routeId, CancellationToken ct) =>
        InvokeListenersAsync(nameof(IRouteLifecycleListener.OnRouteSuspending),
            l => l.OnRouteSuspending(routeId, ct), failFast: false);

    private Task NotifyRouteErrored(string routeId, Exception error, CancellationToken ct) =>
        InvokeListenersAsync(nameof(IRouteLifecycleListener.OnRouteErrored),
            l => l.OnRouteErrored(routeId, error, ct), failFast: false);

    internal Task NotifyExchangeTimedOut(string routeId, string exchangeId, TimeSpan elapsed, CancellationToken ct) =>
        InvokeListenersAsync(nameof(IRouteLifecycleListener.OnExchangeTimedOut),
            l => l.OnExchangeTimedOut(routeId, exchangeId, elapsed, ct), failFast: false);

    // Exchange-level events — published by ExchangeEventsProcessor (outermost route wrapper).
    // Hot path: callers skip the call entirely when HasLifecycleListeners is false.

    internal bool HasLifecycleListeners => Volatile.Read(ref _lifecycleListeners).Length > 0;

    internal Task NotifyExchangeReceived(string routeId, IExchange exchange, CancellationToken ct) =>
        InvokeListenersAsync(nameof(IRouteLifecycleListener.OnExchangeReceived),
            l => l.OnExchangeReceived(routeId, exchange, ct), failFast: false);

    internal Task NotifyExchangeCompleted(string routeId, IExchange exchange, CancellationToken ct) =>
        InvokeListenersAsync(nameof(IRouteLifecycleListener.OnExchangeCompleted),
            l => l.OnExchangeCompleted(routeId, exchange, ct), failFast: false);

    internal Task NotifyExchangeFailed(string routeId, IExchange exchange, Exception exception, CancellationToken ct) =>
        InvokeListenersAsync(nameof(IRouteLifecycleListener.OnExchangeFailed),
            l => l.OnExchangeFailed(routeId, exchange, exception, ct), failFast: false);
}

/// <summary>
/// Possible statuses for a compiled route.
/// </summary>
public enum RouteStatus
{
    /// <summary>Route is stopped.</summary>
    Stopped,

    /// <summary>Route is started and processing exchanges.</summary>
    Started,

    /// <summary>Route is starting (consumer.Start() in progress).</summary>
    Starting,

    /// <summary>Route is suspending (Stop() called, notifying lifecycle listeners).</summary>
    Suspending,

    /// <summary>Route is suspended (consumer draining in-flight exchanges, no new messages accepted).</summary>
    Suspended,

    /// <summary>Route is stopping (drain timeout expired, force-cancelling).</summary>
    Stopping,

    /// <summary>Route startup or compilation failed.</summary>
    Errored
}

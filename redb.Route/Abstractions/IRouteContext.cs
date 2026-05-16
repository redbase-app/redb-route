using redb.Route.Core;

namespace redb.Route.Abstractions;

/// <summary>
/// Central runtime context for route management: endpoint cache, component registry,
/// named object registry, context-level properties, service locator, and route lifecycle.
/// Provides three-tier exception handling (local route → global → error handler fallback).
/// Thread-safe via <see cref="System.Collections.Concurrent.ConcurrentDictionary{TKey,TValue}"/>.
/// </summary>
public interface IRouteContext : IDisposable
{
    // ── Identity ──

    /// <summary>Unique context identifier (GUID by default).</summary>
    string ContextId { get; }

    /// <summary>Whether <see cref="Start"/> has been called and the context is running.</summary>
    bool IsStarted { get; }

    // ── Endpoint Management ──

    /// <summary>Resolves or creates an endpoint by URI string. Caches by NormalizedKey.</summary>
    /// <param name="uri">Endpoint URI (e.g., "direct:orders", "kafka://topic?brokers=localhost").</param>
    /// <returns>Cached or newly created endpoint.</returns>
    IEndpoint GetEndpoint(string uri);

    /// <summary>Returns a snapshot of all currently cached endpoints.</summary>
    IReadOnlyList<IEndpoint> GetEndpoints();

    // ── Component Management ──

    /// <summary>Registers a component for its URI scheme.</summary>
    /// <param name="component">The component to register.</param>
    void AddComponent(IComponent component);

    /// <summary>Gets a component by type, optionally filtering by scheme name.</summary>
    /// <typeparam name="T">Component type.</typeparam>
    /// <param name="scheme">Optional scheme to match. If null, returns first matching type.</param>
    /// <returns>The component, or <c>default</c> if not found.</returns>
    T? GetComponent<T>(string? scheme = null) where T : class, IComponent;

    /// <summary>Returns whether a component with the specified scheme is registered.</summary>
    bool HasComponent(string scheme);

    /// <summary>Returns a snapshot of all registered component scheme names.</summary>
    IReadOnlyList<string> GetComponentNames();

    /// <summary>Removes a component by scheme. Returns <c>true</c> if removed.</summary>
    bool RemoveComponent(string scheme);

    // ── Properties (context-level shared state) ──

    /// <summary>Gets or sets a context-level property by key. Returns <c>null</c> if not found.</summary>
    object? this[string key] { get; set; }

    /// <summary>Gets a typed property value with automatic conversion.</summary>
    /// <typeparam name="T">Target type.</typeparam>
    /// <param name="key">Property key.</param>
    /// <returns>Converted value, or <c>default</c>.</returns>
    T? GetProperty<T>(string key);

    /// <summary>Sets a typed property. Returns this context for chaining.</summary>
    IRouteContext SetProperty<T>(string key, T value);

    // ── Named Object Registry ──

    /// <summary>Stores a named object in the registry (factories, shared state, etc.).</summary>
    /// <param name="key">Registry key.</param>
    /// <param name="value">Object to store.</param>
    /// <returns>This context for chaining.</returns>
    IRouteContext AddToRegistry(string key, object value);

    /// <summary>Retrieves a typed object from the registry.</summary>
    /// <typeparam name="T">Expected type.</typeparam>
    /// <param name="key">Registry key. A leading <c>#</c> prefix (registry reference) is stripped automatically.</param>
    /// <returns>The object, or <c>default</c> if not found or wrong type.</returns>
    T? GetFromRegistry<T>(string key);

    // ── Service Locator ──

    /// <summary>Registers a service instance by type.</summary>
    /// <param name="serviceType">Service type key.</param>
    /// <param name="service">Service instance.</param>
    void AddService(Type serviceType, object service);

    /// <summary>Retrieves a service by type.</summary>
    /// <typeparam name="T">Service type.</typeparam>
    /// <returns>Service instance, or <c>default</c> if not registered.</returns>
    T? GetService<T>() where T : class;

    // ── Dependency Injection ──

    /// <summary>
    /// Stores an <see cref="IServiceProvider"/> for DI container integration.
    /// Services resolved from this provider are available to processors and components.
    /// </summary>
    /// <param name="serviceProvider">DI service provider.</param>
    /// <returns>This context for chaining.</returns>
    IRouteContext SetServiceProvider(IServiceProvider serviceProvider);

    /// <summary>
    /// Retrieves the registered <see cref="IServiceProvider"/>, or <c>null</c> if not set.
    /// </summary>
    IServiceProvider? GetServiceProvider();

    // ── Exception Handling ──

    /// <summary>
    /// Gets or sets the last-resort error handler invoked when no local or global
    /// exception handler matches. Set to <c>null</c> to disable.
    /// </summary>
    IErrorHandler? ErrorHandler { get; set; }

    /// <summary>
    /// Checks whether an exception handler is registered (globally or locally)
    /// for the specified exception type.
    /// </summary>
    /// <typeparam name="TException">Exception type to check.</typeparam>
    /// <returns><c>true</c> if a handler is registered.</returns>
    bool HasExceptionRoute<TException>() where TException : Exception;

    /// <summary>
    /// Handles an exception using three-tier search order:
    /// 1. Local route handler (by <see cref="IExchange.RouteId"/>).
    /// 2. Global handler (registered via <see cref="AddGlobalExceptionHandler{TException}"/>).
    /// 3. Fallback error handler.
    /// Walks the exception type hierarchy to find the best matching handler.
    /// </summary>
    /// <param name="exchange">The exchange that caused the exception.</param>
    /// <param name="exception">The exception to handle.</param>
    /// <param name="ct">Cancellation token.</param>
    Task HandleException(IExchange exchange, Exception exception, CancellationToken ct = default);

    /// <summary>Registers a global exception handler for a specific exception type.</summary>
    /// <typeparam name="TException">Exception type to handle.</typeparam>
    /// <param name="processor">Processor to invoke when this exception type is caught.</param>
    void AddGlobalExceptionHandler<TException>(IProcessor processor) where TException : Exception;

    /// <summary>
    /// Registers a global exception handler for the specified exception type (non-generic overload).
    /// </summary>
    /// <param name="exceptionType">Exception type to handle.</param>
    /// <param name="processor">Processor to invoke when the exception is caught.</param>
    void AddGlobalExceptionHandler(Type exceptionType, IProcessor processor);

    /// <summary>Registers a local exception handler for a specific route and exception type.</summary>
    /// <param name="routeId">Route identifier.</param>
    /// <param name="exceptionType">Exception type to handle.</param>
    /// <param name="processor">Processor to invoke when this exception type is caught in this route.</param>
    void AddLocalExceptionHandler(string routeId, Type exceptionType, IProcessor processor);

    // ── Routes ──

    /// <summary>Returns a snapshot of all compiled routes managed by this context.</summary>
    IReadOnlyList<CompiledRoute> Routes { get; }

    // ── Lifecycle ──

    /// <summary>Registers a lifecycle listener for route and context state change notifications.</summary>
    /// <param name="listener">Listener to add.</param>
    /// <returns>This context for chaining.</returns>
    IRouteContext AddLifecycleListener(IRouteLifecycleListener listener);

    /// <summary>
    /// Registers a factory that can create <see cref="IRoutePolicy"/> instances for routes
    /// during compilation. When multiple factories are registered, the first non-null result wins.
    /// </summary>
    /// <param name="factory">Policy factory to add.</param>
    /// <returns>This context for chaining.</returns>
    IRouteContext AddRoutePolicyFactory(IRoutePolicyFactory factory);

    /// <summary>
    /// Returns the effective cluster-policy resolution for a compiled route.
    /// </summary>
    /// <param name="routeId">Route identifier.</param>
    /// <returns>
    /// A <see cref="RoutePolicyDescriptor"/> describing whether <c>.Cluster(true)</c> was requested
    /// and which policy (if any) was actually attached. Returns a descriptor with
    /// <see cref="RoutePolicyDescriptor.EffectivePolicy"/> = <c>"AllNodes"</c> and
    /// <see cref="RoutePolicyDescriptor.Reason"/> = <c>"Route not found"</c> when the route is unknown.
    /// </returns>
    RoutePolicyDescriptor GetRoutePolicy(string routeId);

    /// <summary>
    /// Starts all registered endpoints and consumers. Uses degraded-mode startup:
    /// logs individual endpoint failures but continues starting remaining endpoints.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    Task Start(CancellationToken ct = default);

    /// <summary>Stops all routes managed by this context.</summary>
    /// <param name="ct">Cancellation token.</param>
    Task Stop(CancellationToken ct = default);

    /// <summary>Starts an individual route by its route ID.</summary>
    /// <param name="routeId">The route identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    Task StartRoute(string routeId, CancellationToken ct = default);

    /// <summary>Stops an individual route by its route ID.</summary>
    /// <param name="routeId">The route identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    Task StopRoute(string routeId, CancellationToken ct = default);

    /// <summary>Stops an individual route with an optional timeout override.</summary>
    /// <param name="routeId">The route identifier.</param>
    /// <param name="timeout">Timeout for graceful drain. If null, uses default shutdown timeout.</param>
    /// <param name="ct">Cancellation token.</param>
    Task StopRoute(string routeId, TimeSpan? timeout, CancellationToken ct = default);
}

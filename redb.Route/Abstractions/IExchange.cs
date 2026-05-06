namespace redb.Route.Abstractions;

/// <summary>
/// Represents a message exchange in the route pipeline.
/// Contains In message (always present), optional Out message (lazy),
/// exchange pattern, route-level properties, and error state.
/// Supports both C# idiomatic properties and Java-style methods.
/// </summary>
public interface IExchange : IAsyncDisposable
{
    // ── C# idiomatic API ──

    /// <summary>Primary message flowing through the pipeline. Always non-null.</summary>
    IMessage In { get; set; }

    /// <summary>
    /// Response message from request-reply or explicit .Respond().
    /// Null when InOnly (default). Lazy — not allocated until needed.
    /// </summary>
    IMessage? Out { get; set; }

    /// <summary>Current exchange pattern. Default = InOnly.</summary>
    ExchangePattern Pattern { get; set; }

    /// <summary>Whether Out is populated (non-null).</summary>
    bool HasOut => Out != null;

    /// <summary>
    /// Route-level metadata (RouteId, TRANSACT_ACTION, etc.).
    /// Does NOT travel to brokers — use In.Headers for that.
    /// </summary>
    IDictionary<string, object?> Properties { get; }

    /// <summary>Gets a property value with type conversion.</summary>
    /// <typeparam name="T">Target type for the property value.</typeparam>
    /// <param name="key">Property key.</param>
    /// <returns>Converted value or default if not found.</returns>
    T? GetProperty<T>(string key);

    /// <summary>Current exception if processing failed.</summary>
    Exception? Exception { get; set; }

    /// <summary>Whether the current exception has been handled by error handling logic.</summary>
    bool ExceptionHandled { get; set; }

    /// <summary>Identifier of the route this exchange belongs to.</summary>
    string? RouteId { get; set; }

    /// <summary>Unique identifier of this exchange instance, generated at creation.</summary>
    string ExchangeId { get; }

    /// <summary>Whether processing of this exchange has been stopped.</summary>
    bool IsStopped { get; }

    /// <summary>Stops further processing of this exchange in the pipeline.</summary>
    void Stop();

    /// <summary>Deep copy of the exchange including In, Out, Properties.</summary>
    IExchange Clone();

    // ── DI Scope ──

    /// <summary>
    /// Optional scoped service provider bound to this exchange's lifetime.
    /// Null when running without DI (tests, standalone). Processors can resolve
    /// scoped services (DbContext, IRedbService, etc.) from this provider.
    /// </summary>
    IServiceProvider? ServiceProvider => null;

    /// <summary>
    /// Creates a child exchange with the given message, inheriting Properties, RouteId,
    /// Pattern, and scope factory. A new DI scope is created for the child.
    /// </summary>
    IExchange CreateChild(IMessage message)
    {
        var child = Clone();
        child.In = message;
        return child;
    }

    /// <summary>
    /// Creates a child exchange that shares the parent's DI scope (no new scope).
    /// Use for sequential processing where children should reuse the same DB connection
    /// and participate in the parent's transaction.
    /// The child does NOT own the scope and will not dispose it.
    /// </summary>
    IExchange CreateLinkedChild(IMessage message) => CreateChild(message);

    /// <summary>
    /// Creates a deep copy of the exchange that shares the parent's DI scope (no new scope).
    /// Like <see cref="Clone"/> but without creating a new DI scope.
    /// Use for sequential fan-out (Multicast, ScatterGather, RecipientList) where
    /// each target should reuse the same DB connection and transaction.
    /// </summary>
    IExchange CloneLinked() => Clone();

    /// <summary>
    /// Releases DI scopes (main scope + named __redb_scope:* entries) without
    /// disposing the exchange body or headers. Call after processing to free DB
    /// connections early while keeping message data alive for aggregation.
    /// Idempotent — safe to call multiple times.
    /// </summary>
    ValueTask ReleaseScopes() => default;

    /// <summary>Disposes the exchange and its associated DI scope (if any).</summary>
    ValueTask IAsyncDisposable.DisposeAsync() => default;

    // ── Java-style API (kept forever, default interface methods) ──

    /// <summary>Java-style: returns In message.</summary>
    IMessage getIn() => In;

    /// <summary>Java-style: returns Out message.</summary>
    IMessage? getOut() => Out;

    /// <summary>Java-style: returns property value by key.</summary>
    object? getProperty(string key) => Properties.TryGetValue(key, out var v) ? v : null;

    /// <summary>Java-style: returns property value cast to <typeparamref name="T"/>.</summary>
    T? getProperty<T>(string key) => GetProperty<T>(key);

    /// <summary>Java-style: sets property value.</summary>
    void setProperty(string key, object? value) => Properties[key] = value;

    /// <summary>Java-style: returns true if route processing is stopped.</summary>
    bool isStop() => IsStopped;

    /// <summary>Java-style: stops route processing (alias for Stop).</summary>
    void RouteStop() => Stop();

    /// <summary>Java-style: deep copy (alias for Clone).</summary>
    IExchange copy() => Clone();
}

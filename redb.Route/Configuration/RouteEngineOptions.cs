namespace redb.Route.Configuration;

/// <summary>
/// Root configuration options for the route engine.
/// Bound from configuration section "RedbRoute".
/// </summary>
public sealed class RouteEngineOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "RedbRoute";

    /// <summary>
    /// Whether to enable OpenTelemetry instrumentation on route pipelines.
    /// Default: <c>true</c>.
    /// </summary>
    public bool EnableTelemetry { get; set; } = true;

    /// <summary>
    /// Whether to enable metrics collection (exchange count, duration, failures).
    /// Default: <c>true</c>.
    /// </summary>
    public bool EnableMetrics { get; set; } = true;

    /// <summary>
    /// Graceful shutdown timeout when stopping routes.
    /// Default: 30 seconds.
    /// </summary>
    public TimeSpan ShutdownTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Whether to throw on startup if a route fails to compile.
    /// When <c>false</c>, failed routes are logged and skipped.
    /// Default: <c>true</c>.
    /// </summary>
    public bool ThrowOnCompilationError { get; set; } = true;

    /// <summary>
    /// Whether the engine performs cross-cutting startup checks during route compilation
    /// and emits warnings for misconfigurations (e.g., a route declares <c>.Cluster(true)</c>
    /// but no <see cref="Abstractions.IRoutePolicyFactory"/> is registered, so it would run
    /// on all nodes despite the intent).
    /// Default: <c>true</c>. Set to <c>false</c> in tests that intentionally exercise mis-wiring.
    /// </summary>
    public bool StartupChecks { get; set; } = true;

    /// <summary>
    /// Default per-exchange processing timeout applied to all routes
    /// that don't specify their own via <c>.ProcessingTimeout()</c>.
    /// Default: <see cref="Timeout.InfiniteTimeSpan"/> (no timeout).
    /// </summary>
    public TimeSpan DefaultProcessingTimeout { get; set; } = Timeout.InfiniteTimeSpan;

    /// <summary>
    /// Stream caching options. When <see cref="StreamCacheOptions.Enabled"/> is true,
    /// all routes automatically wrap Stream bodies with a seekable cache.
    /// </summary>
    public StreamCacheOptions StreamCaching { get; set; } = new();
}

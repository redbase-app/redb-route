using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.Processors;

namespace redb.Route.Definitions;

/// <summary>
/// Scope-opener definition for the CircuitBreaker EIP.
/// Wraps the downstream pipeline with circuit-breaker protection.
/// Optionally define a fallback branch with <see cref="OnFallback"/>.
/// Inherits the leaf DSL from <see cref="RouteDefinitionBase{TSelf}"/>;
/// child steps build the <em>main</em> downstream pipeline.
/// Close with <see cref="EndCircuitBreaker"/>.
/// </summary>
public class CircuitBreakerDefinition : RouteDefinitionBase<CircuitBreakerDefinition>, IRouteScope
{
    private int _failureThreshold = 5;
    private TimeSpan? _resetTimeout;
    private int _halfOpenMaxCalls = 1;
    internal FallbackDefinition? FallbackBlock;

    internal CircuitBreakerDefinition() { }

    // ── Options ─────────────────────────────────────────────────────────────────

    /// <summary>Sets the number of consecutive failures before the circuit opens (default: 5).</summary>
    public CircuitBreakerDefinition FailureThreshold(int threshold)
    {
        if (threshold <= 0) throw new ArgumentOutOfRangeException(nameof(threshold));
        _failureThreshold = threshold;
        return this;
    }

    /// <summary>Sets the time to wait in Open state before probing (default: 30 s).</summary>
    public CircuitBreakerDefinition ResetTimeout(TimeSpan timeout) { _resetTimeout = timeout; return this; }

    /// <summary>Sets the maximum probe calls allowed in HalfOpen state (default: 1).</summary>
    public CircuitBreakerDefinition HalfOpenMaxCalls(int count)
    {
        if (count <= 0) throw new ArgumentOutOfRangeException(nameof(count));
        _halfOpenMaxCalls = count;
        return this;
    }

    /// <summary>Alias for <see cref="FailureThreshold"/> matching the Apache Camel DSL name.</summary>
    public CircuitBreakerDefinition Threshold(int threshold) => FailureThreshold(threshold);

    // ── Fallback scope ─────────────────────────────────────────────────────────

    /// <summary>
    /// Opens the fallback scope. Build fallback steps via leaf methods,
    /// then call <see cref="FallbackDefinition.EndFallback"/> or
    /// <see cref="FallbackDefinition.EndCircuitBreaker"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown if OnFallback was already called.</exception>
    public FallbackDefinition OnFallback()
    {
        if (FallbackBlock != null)
            throw new InvalidOperationException("OnFallback() can only be called once per CircuitBreaker scope.");
        FallbackBlock = new FallbackDefinition(this);
        return FallbackBlock;
    }

    /// <summary>
    /// Apache Camel nested-lambda style: opens the fallback scope, runs <paramref name="configure"/>
    /// against it, and returns the parent <see cref="CircuitBreakerDefinition"/> so the chain may continue.
    /// </summary>
    public CircuitBreakerDefinition FallBack(Action<FallbackDefinition> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var fb = OnFallback();
        configure(fb);
        return this;
    }

    // ── Navigation ─────────────────────────────────────────────────────────────

    /// <summary>Closes the CircuitBreaker scope and returns the parent route definition.</summary>
    public IRouteDefinition EndCircuitBreaker()
        => (IRouteDefinition)(Parent ?? throw new InvalidOperationException(
            "EndCircuitBreaker() called without a parent route."));

    /// <inheritdoc cref="IRouteScope.End"/>
    public IRouteDefinition End() => EndCircuitBreaker();

    // ── IProcessorDefinition ───────────────────────────────────────────────────

    /// <inheritdoc />
    public override IProcessor CreateProcessor(IRouteContext context)
    {
        IProcessor downstream = BuildPipeline(Outputs, context);
        IProcessor? fallback = FallbackBlock != null ? BuildPipeline(FallbackBlock.Outputs, context) : null;
        var loggerFactory = context.GetService<ILoggerFactory>();
        var logger = loggerFactory?.CreateLogger<CircuitBreakerProcessor>();
        return new CircuitBreakerProcessor(downstream, _failureThreshold, _resetTimeout, _halfOpenMaxCalls, fallback, logger);
    }

    internal static IProcessor BuildPipeline(IList<IProcessorDefinition> outputs, IRouteContext context)
        => NodePipeline.Body(context, outputs);
}

/// <summary>
/// The fallback branch inside a <see cref="CircuitBreakerDefinition"/>.
/// Inherits the leaf DSL from <see cref="RouteDefinitionBase{TSelf}"/>; child steps
/// build the fallback pipeline executed when the circuit is open.
/// Close with <see cref="EndFallback"/> or <see cref="EndCircuitBreaker"/>.
/// </summary>
public class FallbackDefinition : RouteDefinitionBase<FallbackDefinition>, IRouteScope
{
    private readonly CircuitBreakerDefinition _circuitBreaker;

    internal FallbackDefinition(CircuitBreakerDefinition circuitBreaker)
    {
        _circuitBreaker = circuitBreaker;
        Parent = circuitBreaker;
    }

    // ── Navigation ─────────────────────────────────────────────────────────────

    /// <summary>Closes the fallback branch and returns to the parent CircuitBreaker definition.</summary>
    public CircuitBreakerDefinition EndFallback() => _circuitBreaker;

    /// <summary>Closes the entire CircuitBreaker scope.</summary>
    public IRouteDefinition EndCircuitBreaker() => _circuitBreaker.EndCircuitBreaker();

    /// <inheritdoc cref="IRouteScope.End"/>
    public IRouteDefinition End() => EndCircuitBreaker();

    /// <inheritdoc />
    public override IProcessor CreateProcessor(IRouteContext context)
        => throw new InvalidOperationException(
            "FallbackDefinition is compiled via its parent CircuitBreakerDefinition.CreateProcessor.");
}

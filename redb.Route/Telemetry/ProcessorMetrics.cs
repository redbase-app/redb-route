using System.Diagnostics.Metrics;

namespace redb.Route.Telemetry;

/// <summary>
/// Metrics instruments for EIP processors (CircuitBreaker, Throttle, Filter, Debounce, etc.).
/// Subscribe to meter <see cref="RouteMetrics.MeterName"/> ("redb.Route") to capture these.
/// </summary>
public static class ProcessorMetrics
{
    // ── Circuit Breaker ─────────────────────────────────────────
    /// <summary>Number of times a circuit breaker tripped to Open state.</summary>
    public static readonly Counter<long> CircuitBreakerTripped =
        RouteMetrics.Meter.CreateCounter<long>("redb.route.circuitbreaker.tripped", "times", "Circuit breaker state transitions to Open.");

    /// <summary>Exchanges rejected while circuit is Open.</summary>
    public static readonly Counter<long> CircuitBreakerRejected =
        RouteMetrics.Meter.CreateCounter<long>("redb.route.circuitbreaker.rejected", "exchanges", "Exchanges rejected by an open circuit breaker.");

    // ── Throttle ────────────────────────────────────────────────
    /// <summary>Exchanges delayed by throttle (rate limiter).</summary>
    public static readonly Counter<long> ThrottleDelayed =
        RouteMetrics.Meter.CreateCounter<long>("redb.route.throttle.delayed", "exchanges", "Exchanges delayed due to rate limiting.");

    // ── Filter ──────────────────────────────────────────────────
    /// <summary>Exchanges dropped by filter predicates.</summary>
    public static readonly Counter<long> FilterDropped =
        RouteMetrics.Meter.CreateCounter<long>("redb.route.filter.dropped", "exchanges", "Exchanges dropped by a filter predicate.");

    // ── Debounce ────────────────────────────────────────────────
    /// <summary>Exchanges discarded (superseded by a newer message for the same key).</summary>
    public static readonly Counter<long> DebounceDiscarded =
        RouteMetrics.Meter.CreateCounter<long>("redb.route.debounce.discarded", "exchanges", "Exchanges replaced before the quiet period elapsed.");

    /// <summary>Exchanges flushed (forwarded after quiet period).</summary>
    public static readonly Counter<long> DebounceFlushed =
        RouteMetrics.Meter.CreateCounter<long>("redb.route.debounce.flushed", "exchanges", "Exchanges forwarded after the debounce quiet period.");

    // ── Splitter ────────────────────────────────────────────────
    /// <summary>Total parts produced by splitter processors.</summary>
    public static readonly Counter<long> SplitterParts =
        RouteMetrics.Meter.CreateCounter<long>("redb.route.splitter.parts", "parts", "Total parts produced by splitter processors.");

    // ── Timeout ─────────────────────────────────────────────────
    /// <summary>Exchanges that exceeded the configured timeout.</summary>
    public static readonly Counter<long> TimeoutExpired =
        RouteMetrics.Meter.CreateCounter<long>("redb.route.timeout.expired", "exchanges", "Exchanges that exceeded their processing timeout.");
}
